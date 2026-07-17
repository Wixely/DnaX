using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnaX.Caching;

internal interface IDnaXCacheRefreshQueue
{
    bool TryQueue(Func<CancellationToken, ValueTask> work);
}

internal sealed class DnaXCacheRefreshService : BackgroundService, IDnaXCacheRefreshQueue
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _channel =
        Channel.CreateUnbounded<Func<CancellationToken, ValueTask>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ILogger<DnaXCacheRefreshService> _logger;

    public DnaXCacheRefreshService(ILogger<DnaXCacheRefreshService>? logger = null) =>
        _logger = logger ?? NullLogger<DnaXCacheRefreshService>.Instance;

    public bool TryQueue(Func<CancellationToken, ValueTask> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _channel.Writer.TryWrite(work);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (Func<CancellationToken, ValueTask> work in
            _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await work(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "An unhandled DNA X cache refresh operation failed.");
            }
        }
    }
}
