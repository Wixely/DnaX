namespace DnaX.MCPFab;

/// <summary>
/// A server's identity. Deliberately data, not behaviour: MCPFab unifies the shape of every
/// server while each one keeps the identity its users and MCPHub already depend on.
/// </summary>
/// <param name="Name">
/// Canonical product name, for example <c>RedisMCPSharp</c>. This is load-bearing beyond this
/// process: MCPHub derives the config file name (<c>{Name}.json</c>), the executable name, and
/// the release asset name from it. Changing it breaks installation and supervision.
/// </param>
/// <param name="EnvPrefix">
/// Environment variable prefix, for example <c>REDISMCP_</c>. These are hand-abbreviated across
/// the estate (<c>AZDOMCP_</c>, <c>HAMCP_</c>) rather than derived, so it is declared, not computed.
/// </param>
/// <param name="DefaultPort">
/// Port used when configuration supplies none. The effective port always comes from
/// <c>Server:Port</c> in the product config; MCPHub reads that file rather than holding its own copy.
/// </param>
/// <param name="DefaultPath">MCP endpoint path. MCPHub hard-codes <c>/mcp</c>, so changing this breaks it.</param>
/// <param name="LogFilePrefix">
/// Prefix for rolling log files, for example <c>redismcp</c> giving <c>logs/redismcp-.log</c>.
/// Defaults to the lower-cased name. It must stay unique per product: MCPHub runs every server
/// from one shared folder, so a collision means two servers writing the same shared log file.
/// </param>
public sealed record McpFabProduct(
    string Name,
    string EnvPrefix,
    int DefaultPort,
    string DefaultPath = "/mcp",
    string? LogFilePrefix = null)
{
    /// <summary>Effective rolling-log prefix.</summary>
    public string EffectiveLogFilePrefix =>
        string.IsNullOrWhiteSpace(LogFilePrefix) ? Name.ToLowerInvariant() : LogFilePrefix;

    /// <summary>Product configuration file name. MCPHub locates this exact name.</summary>
    public string ConfigFileName => $"{Name}.json";
}
