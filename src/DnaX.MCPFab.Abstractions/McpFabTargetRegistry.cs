namespace DnaX.MCPFab;

/// <summary>
/// A resolved set of configured targets with alias handling, default selection and validation.
/// </summary>
/// <remarks>
/// Replaces the registry that Redis, SQL, Bambu, Portainer and MailCal each wrote separately - the
/// same class with the noun swapped, sharing even the wording of their documentation. Both config
/// shapes in use are supported: a list whose entries carry an <c>Alias</c>, and a dictionary whose
/// key is the alias.
/// </remarks>
/// <typeparam name="TEntry">The server's own entry type.</typeparam>
public sealed class McpFabTargetRegistry<TEntry>
    where TEntry : McpFabTargetEntry
{
    private readonly Dictionary<string, TEntry> _byAlias;

    private McpFabTargetRegistry(
        IReadOnlyList<TEntry> entries,
        Dictionary<string, TEntry> byAlias,
        TEntry? defaultEntry,
        IReadOnlyList<string> problems)
    {
        Entries = entries;
        _byAlias = byAlias;
        Default = defaultEntry;
        Problems = problems;
    }

    /// <summary>All configured entries, enabled or not, in configuration order.</summary>
    public IReadOnlyList<TEntry> Entries { get; }

    /// <summary>Entries that are enabled and therefore resolvable.</summary>
    public IEnumerable<TEntry> EnabledEntries => Entries.Where(entry => entry.Enabled);

    /// <summary>
    /// Target used when a tool omits an alias: the configured default, else the first enabled entry.
    /// </summary>
    public TEntry? Default { get; }

    /// <summary>
    /// Configuration problems found while building, such as duplicate or malformed aliases. Empty
    /// when the registry is sound. Surface these at startup rather than on first tool call.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Number of enabled entries.</summary>
    public int Count => _byAlias.Count;

    /// <summary>Builds a registry from configured entries.</summary>
    /// <param name="entries">Configured entries, in configuration order.</param>
    /// <param name="defaultAlias">Configured default alias, or blank to use the first enabled entry.</param>
    /// <param name="aliasPrefix">Prefix for generated aliases, for example <c>redis</c>.</param>
    /// <param name="policy">Whether a missing alias is generated or is an error.</param>
    public static McpFabTargetRegistry<TEntry> Create(
        IEnumerable<TEntry> entries,
        string? defaultAlias = null,
        string aliasPrefix = "target",
        McpFabAliasPolicy policy = McpFabAliasPolicy.Generate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasPrefix);

        TEntry[] ordered = [.. entries];
        List<string> problems = [];
        Dictionary<string, TEntry> byAlias = new(StringComparer.OrdinalIgnoreCase);
        int generated = 0;

        foreach (TEntry entry in ordered)
        {
            if (string.IsNullOrWhiteSpace(entry.Alias))
            {
                if (policy == McpFabAliasPolicy.Require)
                {
                    problems.Add($"Every {aliasPrefix} entry must declare an Alias.");
                    continue;
                }

                entry.Alias = $"{aliasPrefix}-{++generated}";
            }
            else if (!IsValidAlias(entry.Alias))
            {
                problems.Add(
                    $"Alias '{entry.Alias}' must contain only ASCII letters, digits, '-' or '_' and be at most 64 characters.");
                continue;
            }

            if (!entry.Enabled)
            {
                continue;
            }

            if (!byAlias.TryAdd(entry.Alias, entry))
            {
                problems.Add($"Alias '{entry.Alias}' is duplicated.");
            }
        }

        TEntry? defaultEntry;
        if (string.IsNullOrWhiteSpace(defaultAlias))
        {
            defaultEntry = ordered.FirstOrDefault(entry => entry.Enabled);
        }
        else if (byAlias.TryGetValue(defaultAlias, out TEntry? named))
        {
            defaultEntry = named;
        }
        else
        {
            // Naming a default that does not exist is a configuration mistake, not a reason to
            // silently pick something else - but the server still starts so the operator can fix it.
            problems.Add($"DefaultAlias '{defaultAlias}' does not name an enabled {aliasPrefix} entry.");
            defaultEntry = ordered.FirstOrDefault(entry => entry.Enabled);
        }

        return new McpFabTargetRegistry<TEntry>(ordered, byAlias, defaultEntry, problems);
    }

    /// <summary>Builds a registry from a dictionary whose key is the alias.</summary>
    public static McpFabTargetRegistry<TEntry> CreateFromMap(
        IReadOnlyDictionary<string, TEntry> entries,
        string? defaultAlias = null,
        string aliasPrefix = "target")
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<TEntry> ordered = [];
        foreach ((string key, TEntry entry) in entries)
        {
            entry.Alias = key;
            ordered.Add(entry);
        }

        return Create(ordered, defaultAlias, aliasPrefix, McpFabAliasPolicy.Require);
    }

    /// <summary>Resolves an alias, falling back to <see cref="Default"/> when it is blank.</summary>
    public bool TryResolve(string? alias, out TEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            entry = Default;
            return entry is not null;
        }

        return _byAlias.TryGetValue(alias, out entry);
    }

    /// <summary>
    /// Resolves an alias or throws with a message naming what is configured, so an agent can
    /// correct itself instead of retrying the same unknown alias.
    /// </summary>
    public TEntry Resolve(string? alias)
    {
        if (TryResolve(alias, out TEntry? entry) && entry is not null)
        {
            return entry;
        }

        string known = _byAlias.Count == 0
            ? "none are configured"
            : string.Join(", ", _byAlias.Keys.Order(StringComparer.OrdinalIgnoreCase));

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(alias)
                ? $"No target is configured. Configured aliases: {known}."
                : $"Unknown alias '{alias}'. Configured aliases: {known}.");
    }

    private static bool IsValidAlias(string alias) =>
        alias.Length <= 64 && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
