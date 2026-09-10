namespace DnaX.MCPFab;

/// <summary>
/// The response and timeout bounds every server needs, expressed once.
/// </summary>
/// <remarks>
/// These caps exist to stop a single tool call flooding an agent's context. The concept was
/// reinvented widely: item counts appeared as <c>MaxItems</c>, <c>MaxRows</c>, <c>MaxLogEntries</c>,
/// <c>MaxConsoleBuffer</c> and <c>MaxConsoleEntries</c> (two servers, same concept, different
/// suffix); character caps as <c>MaxJsonChars</c>, <c>MaxValueChars</c>, <c>MaxCellChars</c> and
/// <c>MaximumResponseBytes</c>. <c>RequestTimeoutSeconds</c> was the one name that already agreed,
/// in ten servers, so it is kept verbatim.
/// </remarks>
public sealed class McpFabLimitsOptions
{
    /// <summary>Maximum items in a single tool response.</summary>
    public int MaxItems { get; set; } = 1000;

    /// <summary>Maximum characters in a single serialized tool response.</summary>
    public int MaxChars { get; set; } = 60_000;

    /// <summary>Maximum bytes read from or written to an external system in one call.</summary>
    public long MaxBytes { get; set; } = 2_097_152;

    /// <summary>Page size used when a tool omits one.</summary>
    public int DefaultPageSize { get; set; } = 30;

    /// <summary>Maximum pages a single tool call will walk. Guards against runaway calls.</summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>Timeout for a single outbound request.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>Applies <see cref="MaxItems"/> to a sequence.</summary>
    public IEnumerable<T> Cap<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return MaxItems > 0 ? source.Take(MaxItems) : source;
    }

    /// <summary><see cref="RequestTimeoutSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);
}
