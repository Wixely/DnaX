namespace DnaX.MCPFab;

/// <summary>
/// Base for one configured target in a <see cref="McpFabTargetRegistry{TEntry}"/> - a Redis server,
/// a printer, a Portainer instance, a mail account.
/// </summary>
/// <remarks>
/// Five servers defined this class independently and arrived at the same three members, down to
/// matching documentation wording. Servers add their own connection members by deriving from it.
/// </remarks>
public abstract class McpFabTargetEntry
{
    /// <summary>
    /// Short handle a tool passes to select this target. When blank and the registry generates
    /// aliases, one is assigned at load time as <c>&lt;prefix&gt;-N</c>.
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>Free-text description surfaced by the server's list-targets tool.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this target is usable. Disabled entries are kept but never resolved.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>How a registry treats an entry with no alias.</summary>
public enum McpFabAliasPolicy
{
    /// <summary>Assign <c>&lt;prefix&gt;-N</c> at load time. The behaviour of most servers.</summary>
    Generate,

    /// <summary>Treat a missing alias as a configuration error. The behaviour Kodi and ADB chose.</summary>
    Require,
}
