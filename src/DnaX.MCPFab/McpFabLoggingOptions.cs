namespace DnaX.MCPFab;

/// <summary>
/// The <c>Serilog</c> configuration section, bound as typed options.
/// </summary>
/// <remarks>
/// <para>
/// MCPFab never calls <c>ReadFrom.Configuration</c>. That path is
/// <c>Serilog.Settings.Configuration</c>, which resolves sinks with <c>Assembly.Load</c> plus
/// <c>MethodInfo.Invoke</c> and finds enrichers by scanning <c>DependencyContext.RuntimeLibraries</c> -
/// degraded under <c>PublishSingleFile</c>, broken under trimming, and carrying no trim
/// annotations, so the analyzer reports nothing and the failure is silent.
/// </para>
/// <para>
/// The keys kept here are the ones the estate actually set. The <c>WriteTo</c> array in existing
/// config files stops taking effect: sinks are configured in code, controlled by
/// <see cref="Console"/> and <see cref="File"/>.
/// </para>
/// </remarks>
public sealed class McpFabLoggingOptions
{
    /// <summary>Configuration section name, unchanged from the existing files.</summary>
    public const string SectionName = "Serilog";

    /// <summary>Default minimum level. One of Verbose, Debug, Information, Warning, Error, Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Per-source-context level overrides, for example <c>Microsoft</c> to <c>Warning</c>.
    /// Bound from <c>Serilog:MinimumLevel:Override</c>.
    /// </summary>
    public Dictionary<string, string> Override { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Console sink settings.</summary>
    public McpFabConsoleSinkOptions Console { get; set; } = new();

    /// <summary>Rolling file sink settings.</summary>
    public McpFabFileSinkOptions File { get; set; } = new();
}

/// <summary>Console sink settings.</summary>
public sealed class McpFabConsoleSinkOptions
{
    /// <summary>Whether to write to the console.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether to write every level to standard error instead of standard output.
    /// </summary>
    /// <remarks>
    /// Required for any stdio transport: a console sink writing to stdout interleaves with the
    /// JSON-RPC stream and corrupts it.
    /// </remarks>
    public bool ToStandardError { get; set; }
}

/// <summary>Rolling file sink settings.</summary>
public sealed class McpFabFileSinkOptions
{
    /// <summary>Whether to write a rolling log file.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path template relative to the content root. When blank it is derived from the product's
    /// log prefix, which must stay unique because MCPHub runs every server from one folder with
    /// <c>shared: true</c> - a collision means two servers writing the same file.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Rolled files retained.</summary>
    public int RetainedFileCountLimit { get; set; } = 14;

    /// <summary>Size at which a file rolls, in bytes.</summary>
    public long FileSizeLimitBytes { get; set; } = 52_428_800;
}
