using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DnaX.MCPFab;

/// <summary>A built MCPFab server.</summary>
public sealed class McpFabApp
{
    private readonly WebApplication _app;
    private readonly McpFabProduct _product;
    private readonly ServerOptions _server;
    private readonly string _contentRoot;
    private readonly bool _isWindowsService;
    private readonly Action<IServiceProvider, IMcpFabBanner>? _banner;

    internal McpFabApp(
        WebApplication app,
        McpFabProduct product,
        ServerOptions server,
        string contentRoot,
        bool isWindowsService,
        Action<IServiceProvider, IMcpFabBanner>? banner)
    {
        _app = app;
        _product = product;
        _server = server;
        _contentRoot = contentRoot;
        _isWindowsService = isWindowsService;
        _banner = banner;
    }

    /// <summary>The underlying application, for anything MCPFab does not wrap.</summary>
    public WebApplication Application => _app;

    /// <summary>
    /// Runs the server, returning the process exit code.
    /// </summary>
    /// <remarks>
    /// Wraps the run in the same try/catch/finally every server had: a failure is logged as fatal
    /// and returns 1 rather than surfacing an unhandled exception, and the log is always flushed.
    /// </remarks>
    public async Task<int> RunAsync()
    {
        try
        {
            WriteBanner();
            await _app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private void WriteBanner()
    {
        Microsoft.Extensions.Logging.ILogger logger = _app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(_product.Name);

        logger.LogInformation("{Product} starting", _product.Name);

        McpFabBanner banner = new(logger);
        banner.Line("Endpoint", $"http://{_server.Host}:{_server.Port}{_server.Path}");
        banner.Line("Transport", "HTTP (Streamable)");
        banner.Line("Mode", _isWindowsService ? "WindowsService" : "Console");
        banner.Line("Content root", _contentRoot);
        banner.Line("Authentication", string.IsNullOrWhiteSpace(_server.Password) ? "anonymous" : "shared secret");

        if (string.IsNullOrWhiteSpace(_server.Password) && !_server.IsLoopback)
        {
            // Startup validation rejects this, so reaching here means validation was bypassed.
            banner.Warn($"Bound to {_server.Host} with no Server:Password set.");
        }

        _banner?.Invoke(_app.Services, banner);
    }
}
