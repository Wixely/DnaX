using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnaX.Caching;

internal sealed class DnaXCache : IDnaXCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly DnaXCacheOptions _defaults;
    private readonly TimeProvider _timeProvider;
    private readonly IDnaXCacheRefreshQueue _refreshQueue;
    private readonly ILogger<DnaXCache> _logger;

    public DnaXCache(
        IOptions<DnaXCacheOptions> defaults,
        TimeProvider timeProvider,
        IDnaXCacheRefreshQueue refreshQueue,
        ILogger<DnaXCache>? logger = null)
    {
        _defaults = defaults.Value;
        _timeProvider = timeProvider;
        _refreshQueue = refreshQueue;
        _logger = logger ?? NullLogger<DnaXCache>.Instance;
        Validate(_defaults.DefaultDuration, _defaults.DefaultStaleDuration, _defaults.DefaultRefreshFailureCooldown);
    }

    public T Hit<T>(string key, Func<T> factory, DnaXCacheEntryOptions? options = null)
    {
        ValidateArguments(key, factory);
        CachePolicy policy = GetPolicy(options);
        long now = UtcTicks();

        if (TryGetUsable(key, now, out T? value, out CacheEntry? stale))
        {
            if (stale is not null)
            {
                ScheduleRefresh(key, stale, typeof(T), _ => new ValueTask<object?>(factory()), policy);
            }

            return value!;
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            now = UtcTicks();
            if (TryGetUsable(key, now, out value, out stale))
            {
                if (stale is not null)
                {
                    ScheduleRefresh(key, stale, typeof(T), _ => new ValueTask<object?>(factory()), policy);
                }

                return value!;
            }

            T result = factory();
            StoreSuccessfulValue(key, typeof(T), result, policy);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<T> HitAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        DnaXCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(key, factory);
        cancellationToken.ThrowIfCancellationRequested();
        CachePolicy policy = GetPolicy(options);
        long now = UtcTicks();

        if (TryGetUsable(key, now, out T? value, out CacheEntry? stale))
        {
            if (stale is not null)
            {
                ScheduleRefresh(
                    key,
                    stale,
                    typeof(T),
                    async refreshToken => await factory(refreshToken).ConfigureAwait(false),
                    policy);
            }

            return value!;
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = UtcTicks();
            if (TryGetUsable(key, now, out value, out stale))
            {
                if (stale is not null)
                {
                    ScheduleRefresh(
                        key,
                        stale,
                        typeof(T),
                        async refreshToken => await factory(refreshToken).ConfigureAwait(false),
                        policy);
                }

                return value!;
            }

            T result = await factory(cancellationToken).ConfigureAwait(false);
            StoreSuccessfulValue(key, typeof(T), result, policy);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _entries.TryRemove(key, out _);
    }

    public void Clear() => _entries.Clear();

    private bool TryGetUsable<T>(
        string key,
        long now,
        out T? value,
        out CacheEntry? stale)
    {
        value = default;
        stale = null;

        if (!_entries.TryGetValue(key, out CacheEntry? entry))
        {
            return false;
        }

        EnsureType<T>(key, entry);
        if (now <= entry.FreshUntilTicks)
        {
            value = (T?)entry.Value;
            return true;
        }

        if (now <= entry.StaleUntilTicks)
        {
            value = (T?)entry.Value;
            stale = entry;
            return true;
        }

        _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry));
        return false;
    }

    private void ScheduleRefresh(
        string key,
        CacheEntry stale,
        Type valueType,
        Func<CancellationToken, ValueTask<object?>> factory,
        CachePolicy policy)
    {
        long now = UtcTicks();
        if (now < Interlocked.Read(ref stale.RetryAfterTicks) ||
            Interlocked.CompareExchange(ref stale.Refreshing, 1, 0) != 0)
        {
            return;
        }

        bool queued = _refreshQueue.TryQueue(async stoppingToken =>
        {
            try
            {
                stoppingToken.ThrowIfCancellationRequested();
                object? result = await factory(stoppingToken).ConfigureAwait(false);
                if (result is not null || policy.CacheNulls)
                {
                    CacheEntry fresh = CreateEntry(valueType, result, policy);
                    _entries.TryUpdate(key, fresh, stale);
                }
                else
                {
                    _entries.TryRemove(new KeyValuePair<string, CacheEntry>(key, stale));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref stale.Refreshing, 0);
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(
                    ref stale.RetryAfterTicks,
                    AddClamped(UtcTicks(), policy.RefreshFailureCooldown));
                Interlocked.Exchange(ref stale.Refreshing, 0);
                _logger.LogWarning(exception, "Background refresh failed for cache key {CacheKey}.", key);
            }
        });

        if (!queued)
        {
            Interlocked.Exchange(ref stale.Refreshing, 0);
        }
    }

    private void StoreSuccessfulValue<T>(string key, Type valueType, T result, CachePolicy policy)
    {
        if (result is null && !policy.CacheNulls)
        {
            _entries.TryRemove(key, out _);
            return;
        }

        _entries[key] = CreateEntry(valueType, result, policy);
    }

    private CacheEntry CreateEntry(Type valueType, object? value, CachePolicy policy)
    {
        long now = UtcTicks();
        long freshUntil = AddClamped(now, policy.Duration);
        long staleUntil = AddClamped(freshUntil, policy.StaleDuration);
        return new CacheEntry(valueType, value, freshUntil, staleUntil);
    }

    private CachePolicy GetPolicy(DnaXCacheEntryOptions? options)
    {
        CachePolicy policy = options is null
            ? new CachePolicy(
                _defaults.DefaultDuration,
                _defaults.DefaultStaleDuration,
                _defaults.CacheNullsByDefault,
                _defaults.DefaultRefreshFailureCooldown)
            : new CachePolicy(
                options.Duration,
                options.StaleDuration,
                options.CacheNulls,
                options.RefreshFailureCooldown);

        Validate(policy.Duration, policy.StaleDuration, policy.RefreshFailureCooldown);
        return policy;
    }

    private long UtcTicks() => _timeProvider.GetUtcNow().UtcDateTime.Ticks;

    private static long AddClamped(long ticks, TimeSpan duration)
    {
        long addition = duration.Ticks;
        return ticks > DateTime.MaxValue.Ticks - addition
            ? DateTime.MaxValue.Ticks
            : ticks + addition;
    }

    private static void EnsureType<T>(string key, CacheEntry entry)
    {
        if (entry.ValueType != typeof(T))
        {
            throw new DnaXCacheTypeMismatchException(key, entry.ValueType, typeof(T));
        }
    }

    private static void Validate(TimeSpan duration, TimeSpan staleDuration, TimeSpan cooldown)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Cache duration must be positive.");
        }

        if (staleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleDuration), "Stale duration cannot be negative.");
        }

        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "Refresh failure cooldown cannot be negative.");
        }
    }

    private static void ValidateArguments<TDelegate>(string key, TDelegate factory)
        where TDelegate : Delegate
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
    }

    private sealed class CacheEntry(Type valueType, object? value, long freshUntilTicks, long staleUntilTicks)
    {
        public Type ValueType { get; } = valueType;
        public object? Value { get; } = value;
        public long FreshUntilTicks { get; } = freshUntilTicks;
        public long StaleUntilTicks { get; } = staleUntilTicks;
        public int Refreshing;
        public long RetryAfterTicks;
    }

    private readonly record struct CachePolicy(
        TimeSpan Duration,
        TimeSpan StaleDuration,
        bool CacheNulls,
        TimeSpan RefreshFailureCooldown);
}
