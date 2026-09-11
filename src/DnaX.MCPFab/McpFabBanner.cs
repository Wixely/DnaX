using Microsoft.Extensions.Logging;

namespace DnaX.MCPFab;

/// <summary>Writes the startup banner through <see cref="ILogger"/>.</summary>
internal sealed class McpFabBanner : IMcpFabBanner
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    internal McpFabBanner(Microsoft.Extensions.Logging.ILogger logger) => _logger = logger;

    public void Line(string label, string value) =>
        _logger.LogInformation("  {Label}: {Value}", label, value);

    public void Detail(string text) =>
        _logger.LogInformation("  {Detail}", text);

    public void Warn(string text) =>
        _logger.LogWarning("  {Warning}", text);
}
