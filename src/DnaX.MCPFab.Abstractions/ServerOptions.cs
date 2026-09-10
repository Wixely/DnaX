namespace DnaX.MCPFab;

/// <summary>
/// The <c>Server</c> configuration section, identical in shape across every MCPFab server.
/// </summary>
/// <remarks>
/// The section name, the <c>Port</c> key and the <c>Host</c> key are a cross-product contract:
/// MCPHub locates a <c>Server</c> object carrying <c>Port</c> by searching the config document,
/// and builds the endpoint and health URLs from it. Renaming any of them stops MCPHub being able
/// to supervise or proxy the server, without an error that names the cause.
/// </remarks>
public sealed class ServerOptions
{
    /// <summary>Configuration section name. Load-bearing for MCPHub; do not change.</summary>
    public const string SectionName = "Server";

    /// <summary>Interface to bind. <c>localhost</c> binds loopback only; any other value binds publicly.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Listening port.</summary>
    public int Port { get; set; }

    /// <summary>MCP endpoint path. MCPHub hard-codes <c>/mcp</c>.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>Service name used when the host is started by the Windows Service Control Manager.</summary>
    public string WindowsServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret accepted as <c>Authorization: Bearer</c>, HTTP Basic, or <c>X-MCP-Password</c>.
    /// </summary>
    /// <remarks>
    /// Blank is permitted on loopback and rejected when binding beyond it - see
    /// <see cref="ServerOptionsValidator"/>. Blank must stay usable on loopback because MCPHub
    /// launches managed servers with no credentials and registers them with no headers, so
    /// requiring a secret unconditionally would make every managed server unreachable.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>True when <see cref="Host"/> names a loopback interface.</summary>
    public bool IsLoopback => McpFabHostAddress.IsLoopback(Host);
}
