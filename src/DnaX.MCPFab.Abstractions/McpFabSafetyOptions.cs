namespace DnaX.MCPFab;

/// <summary>
/// The safety posture every MCPSharp server has, expressed once.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReadOnly"/> already exists under that exact name, with the same semantics and
/// near-identical documentation, in eighteen of the twenty servers - so it anchors the vocabulary.
/// The second-tier gate did not fare so well: it appears as <c>AllowDangerous</c>,
/// <c>AllowDestructive</c>, <c>AllowDestroy</c>, <c>AllowDelete</c>, <c>AllowPermanentDelete</c>,
/// <c>AllowArbitraryCommands</c>, <c>AllowRawGcode</c> and, with inverted polarity,
/// <c>DisableScriptEvaluation</c>. <see cref="AllowDestructive"/> is the canonical name.
/// </para>
/// <para>
/// Per-category gates were similarly reinvented in five container shapes: flat <c>Allow*</c>
/// properties, a nested <c>Controls</c> object, an <c>Operations</c> map (keyed by snake_case in
/// one server and PascalCase in another), and a separate top-level <c>Policy</c> section.
/// <see cref="Gates"/> replaces all five with one map keyed by tool name.
/// </para>
/// </remarks>
public sealed class McpFabSafetyOptions
{
    /// <summary>
    /// Master safety switch. When true, every mutating tool refuses with an error naming the
    /// config key that would permit it. Default true - flip it deliberately per deployment.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Second-tier gate for the irreversible tail: deletion, arbitrary command execution, and
    /// anything that cannot be undone by running another tool.
    /// </summary>
    /// <remarks>
    /// Layered on purpose rather than folded into <see cref="ReadOnly"/>. These servers can drop
    /// databases, delete stacks across an estate, and move axes on unattended hardware, so
    /// <see cref="ReadOnly"/> being false must not on its own unlock that tail.
    /// </remarks>
    public bool AllowDestructive { get; set; }

    /// <summary>
    /// Per-tool overrides, keyed by tool name (for example <c>redis_del</c>). A tool absent from
    /// the map falls back to <see cref="ReadOnly"/> and <see cref="AllowDestructive"/>.
    /// </summary>
    public Dictionary<string, bool> Gates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a tool may run.
    /// </summary>
    /// <param name="toolName">Tool name as advertised over MCP.</param>
    /// <param name="mutates">Whether the tool changes anything.</param>
    /// <param name="destructive">Whether the change is irreversible.</param>
    public bool IsAllowed(string toolName, bool mutates, bool destructive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // An explicit per-tool gate is the operator's most specific statement of intent, so it
        // wins in both directions.
        if (Gates.TryGetValue(toolName, out bool gate))
        {
            return gate;
        }

        if (!mutates)
        {
            return true;
        }

        return !ReadOnly && (!destructive || AllowDestructive);
    }

    /// <summary>
    /// Builds the refusal message for a blocked tool, naming the key that would permit it so the
    /// caller is told how to proceed rather than only that it failed.
    /// </summary>
    public string DescribeRefusal(string sectionName, string toolName, bool destructive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (Gates.TryGetValue(toolName, out bool gate) && !gate)
        {
            return $"'{toolName}' is disabled by {sectionName}:Gates:{toolName}.";
        }

        return destructive && !AllowDestructive
            ? $"'{toolName}' is destructive and requires {sectionName}:AllowDestructive to be true."
            : $"'{toolName}' changes state and requires {sectionName}:ReadOnly to be false.";
    }
}
