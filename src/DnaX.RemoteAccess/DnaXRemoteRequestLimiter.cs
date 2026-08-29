using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace DnaX.RemoteAccess;

internal sealed class DnaXRemoteRequestLimiter
{
    private readonly ConcurrentDictionary<DnaXRemoteSurface, SemaphoreSlim> _concurrency = new();
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly DnaXRemoteAccessOptions _options;
    private readonly TimeProvider _clock;

    public DnaXRemoteRequestLimiter(IOptions<DnaXRemoteAccessOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public async ValueTask<DnaXRemoteRequestLease?> TryAcquireAsync(
        DnaXRemoteSurface surface,
        string identity,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        string key = $"{surface}:{identity}";
        Window window = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, current) => now - current.Start >= TimeSpan.FromMinutes(1)
                ? new Window(now, 1)
                : current with { Count = current.Count + 1 });
        if (window.Count > _options.Limits.RequestsPerMinutePerIdentity)
        {
            return null;
        }

        SemaphoreSlim semaphore = _concurrency.GetOrAdd(
            surface,
            _ => new SemaphoreSlim(_options.Limits.MaximumConcurrentRequestsPerSurface));
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (_windows.Count > 4_096)
        {
            foreach ((string oldKey, Window oldWindow) in _windows)
            {
                if (now - oldWindow.Start >= TimeSpan.FromMinutes(2))
                {
                    _windows.TryRemove(oldKey, out _);
                }
            }
        }

        return new DnaXRemoteRequestLease(semaphore);
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}

internal sealed class DnaXRemoteRequestLease(SemaphoreSlim semaphore) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        semaphore.Release();
        return ValueTask.CompletedTask;
    }
}
