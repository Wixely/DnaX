using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace DnaX.Redis;

internal sealed class DnaXRedis : IDnaXRedis, IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, Resource> _resources;

    public DnaXRedis(
        IEnumerable<DnaXRedisRegistration> registrations,
        IServiceProvider services,
        ILogger<DnaXRedis>? logger = null)
    {
        logger ??= NullLogger<DnaXRedis>.Instance;
        Dictionary<string, Resource> resources = new(StringComparer.Ordinal);
        foreach (DnaXRedisRegistration registration in registrations)
        {
            if (!resources.TryAdd(registration.Name, new Resource(registration, services, logger)))
            {
                throw new InvalidOperationException($"Redis resource '{registration.Name}' is registered more than once.");
            }
        }

        _resources = resources;
    }

    public T Execute<T>(string name, Func<IDatabase, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        IDatabase database = GetResource(name).GetDatabase();
        return operation(database);
    }

    public void Execute(string name, Action<IDatabase> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation(GetResource(name).GetDatabase());
    }

    public async ValueTask<T> ExecuteAsync<T>(
        string name,
        Func<IDatabase, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase database = await GetResource(name).GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        return await operation(database, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ExecuteAsync(
        string name,
        Func<IDatabase, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase database = await GetResource(name).GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await operation(database, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (Resource resource in _resources.Values)
        {
            resource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Resource resource in _resources.Values)
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Resource GetResource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_resources.TryGetValue(name, out Resource? resource))
        {
            throw new DnaXRedisNotFoundException(name, _resources.Keys);
        }

        return resource;
    }

    private sealed class Resource : IDisposable, IAsyncDisposable
    {
        private readonly DnaXRedisRegistration _registration;
        private readonly Lazy<Task<IConnectionMultiplexer>> _multiplexer;
        private readonly ILogger _logger;
        private int _disposed;

        public Resource(
            DnaXRedisRegistration registration,
            IServiceProvider services,
            ILogger logger)
        {
            _registration = registration;
            _logger = logger;
            _multiplexer = new Lazy<Task<IConnectionMultiplexer>>(
                async () =>
                {
                    IConnectionMultiplexer value = await registration
                        .MultiplexerFactory(services)
                        .ConfigureAwait(false);
                    return value ?? throw new InvalidOperationException(
                        $"The multiplexer factory for '{registration.Name}' returned null.");
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public IDatabase GetDatabase()
        {
            ThrowIfDisposed();
            IConnectionMultiplexer multiplexer = _multiplexer.Value.GetAwaiter().GetResult();
            return multiplexer.GetDatabase(_registration.Database);
        }

        public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            IConnectionMultiplexer multiplexer = await _multiplexer.Value
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return multiplexer.GetDatabase(_registration.Database);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 ||
                !_registration.OwnsMultiplexer ||
                !_multiplexer.IsValueCreated)
            {
                return;
            }

            try
            {
                _multiplexer.Value.GetAwaiter().GetResult().Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to dispose Redis resource {RedisName}.", _registration.Name);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 ||
                !_registration.OwnsMultiplexer ||
                !_multiplexer.IsValueCreated)
            {
                return;
            }

            try
            {
                IConnectionMultiplexer multiplexer = await _multiplexer.Value.ConfigureAwait(false);
                if (multiplexer is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    multiplexer.Dispose();
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to asynchronously dispose Redis resource {RedisName}.", _registration.Name);
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }
}
