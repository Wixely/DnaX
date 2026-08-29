namespace DnaX.RemoteAccess;

public sealed class DnaXRemoteAccessOptions
{
    public bool Enabled { get; set; }

    /// <summary>Stable, deployment-specific identifier used to reject restored state from another deployment.</summary>
    public string? DeploymentId { get; set; }

    /// <summary>Deployment-policy changes are captured at startup and require a restart.</summary>
    public bool ConfigurationChangesRequireRestart => true;

    public DnaXRemoteSurfaceOptions Api { get; set; } = new()
    {
        FixedEndpointPath = "/api/v1",
    };

    public DnaXRemoteSurfaceOptions Mcp { get; set; } = new()
    {
        FixedEndpointPath = "/mcp",
    };

    public DnaXRemoteAuditOptions Audit { get; set; } = new();

    public DnaXRemoteNetworkOptions Network { get; set; } = new();

    public DnaXRemoteLimitOptions Limits { get; set; } = new();
}

public sealed class DnaXRemoteSurfaceOptions
{
    public bool Available { get; set; }

    public bool UseRandomizedEndpoint { get; set; } = true;

    public string FixedEndpointPath { get; set; } = string.Empty;

    public bool AllowRuntimeActivation { get; set; }

    public bool AllowCredentialRotation { get; set; }

    public bool AllowEndpointRotation { get; set; }

    public bool AllowAnonymous { get; set; }
}

public sealed class DnaXRemoteAuditOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

    public int MaximumEvents { get; set; } = 50_000;
}

public sealed class DnaXRemoteNetworkOptions
{
    public bool RequireHttps { get; set; } = true;

    public bool ForwardedHeadersConfigured { get; set; }

    public string? BindingDescription { get; set; }

    public string? TrustedProxyDescription { get; set; }
}

public sealed class DnaXRemoteLimitOptions
{
    public long MaximumRequestBodyBytes { get; set; } = 1_048_576;

    public int MaximumConcurrentRequestsPerSurface { get; set; } = 8;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int MaximumPageSize { get; set; } = 100;

    public int MaximumResultCount { get; set; } = 1_000;

    public int RequestsPerMinutePerIdentity { get; set; } = 60;
}
