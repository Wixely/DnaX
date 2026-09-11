using Serilog;
using Serilog.Events;

namespace DnaX.MCPFab;

/// <summary>
/// Builds Serilog configuration in code from <see cref="McpFabLoggingOptions"/>.
/// </summary>
/// <remarks>
/// Every sink and enricher here is a static call, so the trimmer can see them. That is the whole
/// point: the JSON-driven equivalent resolves them reflectively and fails silently once trimmed.
/// </remarks>
public static class McpFabLogging
{
    internal const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    internal const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates the bootstrap logger used before configuration is available, so a failure during
    /// startup is still recorded rather than lost.
    /// </summary>
    public static ILogger CreateBootstrapLogger(McpFabProduct product, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", $"{product.EffectiveLogFilePrefix}-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: FileTemplate)
            .CreateBootstrapLogger();
    }

    /// <summary>Applies typed options to a logger configuration.</summary>
    public static LoggerConfiguration Configure(
        this LoggerConfiguration logger,
        McpFabLoggingOptions options,
        McpFabProduct product,
        string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(product);

        logger.MinimumLevel.Is(ParseLevel(options.MinimumLevel, LogEventLevel.Information));

        foreach ((string source, string level) in options.Override)
        {
            logger.MinimumLevel.Override(source, ParseLevel(level, LogEventLevel.Information));
        }

        logger
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId();

        if (options.Console.Enabled)
        {
            logger.WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                standardErrorFromLevel: options.Console.ToStandardError ? LogEventLevel.Verbose : null);
        }

        if (options.File.Enabled)
        {
            string path = string.IsNullOrWhiteSpace(options.File.Path)
                ? Path.Combine(contentRoot, "logs", $"{product.EffectiveLogFilePrefix}-.log")
                : Path.Combine(contentRoot, options.File.Path);

            logger.WriteTo.File(
                path,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.File.RetainedFileCountLimit,
                fileSizeLimitBytes: options.File.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: FileTemplate);
        }

        return logger;
    }

    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse(value, ignoreCase: true, out LogEventLevel level) ? level : fallback;
}
