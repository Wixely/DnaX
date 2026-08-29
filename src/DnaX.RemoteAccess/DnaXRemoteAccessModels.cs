using System.Security.Claims;

namespace DnaX.RemoteAccess;

public enum DnaXRemoteSurface
{
    Api,
    Mcp,
}

public enum DnaXRemoteRouteMode
{
    Fixed,
    Randomized,
}

public enum DnaXRemoteAvailability
{
    Disabled,
    UnavailableByDeploymentPolicy,
    Inactive,
    Misconfigured,
    Degraded,
    Available,
}

public enum DnaXRemoteAuditResult
{
    Allowed,
    NotFound,
    Unauthorized,
    Rejected,
    Failed,
}

public sealed record DnaXRemoteCredentialMetadata(
    Guid Id,
    string Suffix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record DnaXRemoteSurfaceState(
    DnaXRemoteSurface Surface,
    string DeploymentId,
    bool IsActive,
    bool AllowAnonymous,
    string? ProtectedRoute,
    string? RouteSuffix,
    long Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record DnaXRemoteStoredCredential(
    Guid Id,
    DnaXRemoteSurface Surface,
    string Algorithm,
    byte[] Hash,
    string Suffix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record DnaXRemoteEffectiveSurface(
    DnaXRemoteSurface Surface,
    DnaXRemoteAvailability Availability,
    bool IsActive,
    bool IsAnonymous,
    DnaXRemoteRouteMode RouteMode,
    string? EndpointPath,
    string? RouteSuffix,
    DnaXRemoteCredentialMetadata? Credential,
    long Version,
    string? Detail = null)
{
    public bool IsAvailable => Availability == DnaXRemoteAvailability.Available;
}

public sealed record DnaXRemoteAdministrationState(
    bool Enabled,
    DnaXRemoteEffectiveSurface Api,
    DnaXRemoteEffectiveSurface Mcp,
    bool AuditEnabled,
    bool RequiresRestart);

public sealed record DnaXRemoteDiagnosticState(
    bool Enabled,
    DnaXRemoteAvailability Api,
    DnaXRemoteAvailability Mcp,
    bool AuditEnabled,
    bool RequireHttps,
    bool ConfigurationChangesRequireRestart);

public sealed record DnaXGeneratedCredential(
    DnaXRemoteSurface Surface,
    string Secret,
    string Suffix,
    DateTimeOffset CreatedAtUtc,
    long Version);

public sealed record DnaXRevokedCredential(
    DnaXRemoteSurface Surface,
    DateTimeOffset RevokedAtUtc,
    long Version);

public sealed record DnaXRemoteAuditEvent(
    DnaXRemoteSurface Surface,
    string? ServiceIdentity,
    string? CredentialSuffix,
    string? Client,
    string Method,
    string Action,
    DnaXRemoteAuditResult Result,
    int StatusCode,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record DnaXRemoteAuditRecord(long Id, DnaXRemoteAuditEvent Event);

public static class DnaXRemoteClaimTypes
{
    public const string Surface = "dnax:remote:surface";
    public const string CredentialSuffix = "dnax:remote:credential_suffix";
    public const string Scope = "dnax:remote:scope";
}

public static class DnaXRemoteAccessPrincipalExtensions
{
    public static bool HasDnaXRemoteScope(this ClaimsPrincipal principal, string scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return principal.Claims.Any(claim =>
            claim.Type == DnaXRemoteClaimTypes.Scope &&
            string.Equals(claim.Value, scope, StringComparison.Ordinal));
    }
}

public sealed class DnaXRemoteConcurrencyException(string message) : InvalidOperationException(message);

public sealed class DnaXRemotePolicyException(string message) : InvalidOperationException(message);
