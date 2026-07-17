namespace DnaX.Diagnostics;

public sealed class DnaXDiagnosticsOptions
{
    /// <summary>Maps the detailed runtime endpoint. Disabled by default.</summary>
    public bool EnableDetails { get; set; }

    /// <summary>Includes registered route patterns in details. Disabled by default.</summary>
    public bool IncludeRoutes { get; set; }

    /// <summary>Requires authorization for the mapped diagnostics group. Enabled by default.</summary>
    public bool RequireAuthorization { get; set; } = true;

    /// <summary>Optional named authorization policy. A null value uses the default policy.</summary>
    public string? AuthorizationPolicy { get; set; }
}
