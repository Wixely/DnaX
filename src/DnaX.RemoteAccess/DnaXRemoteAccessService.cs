using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace DnaX.RemoteAccess;

internal interface IDnaXRemoteAccessRuntime
{
    ValueTask<DnaXRemoteEffectiveSurface> GetSurfaceAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXRemoteAuthentication> AuthenticateAsync(
        DnaXRemoteEffectiveSurface effective,
        string? authorization,
        CancellationToken cancellationToken = default);

    ValueTask RecordAuditAsync(DnaXRemoteAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

internal sealed record DnaXRemoteAuthentication(
    bool Succeeded,
    string? ServiceIdentity,
    string? CredentialSuffix,
    IReadOnlyList<string> Scopes);

internal sealed class DnaXRemoteAccessService :
    IDnaXRemoteAccessAdministration,
    IDnaXRemoteAccessDiagnostics,
    IDnaXRemoteAccessRuntime
{
    private const string HashAlgorithm = "sha256-v1";
    private readonly IDnaXRemoteAccessStore _store;
    private readonly DnaXRemoteAccessOptions _options;
    private readonly IDataProtector _routeProtector;
    private readonly TimeProvider _clock;

    public DnaXRemoteAccessService(
        IDnaXRemoteAccessStore store,
        IOptions<DnaXRemoteAccessOptions> options,
        IDataProtectionProvider dataProtection,
        TimeProvider clock)
    {
        _store = store;
        _options = options.Value;
        _routeProtector = dataProtection.CreateProtector("DnaX.RemoteAccess.Route.v1");
        _clock = clock;
    }

    public async ValueTask<DnaXRemoteAdministrationState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteEffectiveSurface api = await GetSurfaceAsync(DnaXRemoteSurface.Api, cancellationToken)
            .ConfigureAwait(false);
        DnaXRemoteEffectiveSurface mcp = await GetSurfaceAsync(DnaXRemoteSurface.Mcp, cancellationToken)
            .ConfigureAwait(false);
        return new DnaXRemoteAdministrationState(
            _options.Enabled,
            api,
            mcp,
            _options.Audit.Enabled,
            RequiresRestart: false);
    }

    public async ValueTask<DnaXRemoteDiagnosticState> GetDiagnosticStateAsync(
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteEffectiveSurface api = await GetSurfaceAsync(DnaXRemoteSurface.Api, cancellationToken)
            .ConfigureAwait(false);
        DnaXRemoteEffectiveSurface mcp = await GetSurfaceAsync(DnaXRemoteSurface.Mcp, cancellationToken)
            .ConfigureAwait(false);
        return new DnaXRemoteDiagnosticState(
            _options.Enabled,
            api.Availability,
            mcp.Availability,
            _options.Audit.Enabled,
            _options.Network.RequireHttps,
            _options.ConfigurationChangesRequireRestart);
    }

    public async ValueTask<DnaXGeneratedCredential> RotateCredentialAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        IReadOnlyCollection<string>? scopes = null,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteSurfaceOptions policy = Policy(surface);
        EnsureFeatureAvailable(surface, policy);
        if (!policy.AllowCredentialRotation)
        {
            throw new DnaXRemotePolicyException($"Credential rotation is forbidden for {surface} by deployment policy.");
        }

        DnaXRemoteSurfaceState current = await GetStoredSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        EnsureDeploymentBinding(current);
        EnsureVersion(surface, current.Version, expectedVersion);

        DateTimeOffset now = _clock.GetUtcNow();
        if (expiresAtUtc is not null && expiresAtUtc <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Credential expiry must be in the future.");
        }

        string[] normalizedScopes = NormalizeScopes(scopes);
        string prefix = surface == DnaXRemoteSurface.Api ? "dnax_api_" : "dnax_mcp_";
        string secret = prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string suffix = secret[^8..];
        DnaXRemoteStoredCredential credential = new(
            Guid.NewGuid(),
            surface,
            HashAlgorithm,
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)),
            suffix,
            normalizedScopes,
            now,
            expiresAtUtc);

        bool replaced = await _store.TryReplaceCredentialAsync(
            credential,
            DeploymentId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        if (!replaced)
        {
            throw ConcurrentChange(surface);
        }

        return new DnaXGeneratedCredential(surface, secret, suffix, now, checked(expectedVersion + 1));
    }

    public async ValueTask<DnaXRevokedCredential> RevokeCredentialAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteSurfaceOptions policy = Policy(surface);
        EnsureFeatureAvailable(surface, policy);
        if (!policy.AllowCredentialRotation)
        {
            throw new DnaXRemotePolicyException($"Credential revocation is forbidden for {surface} by deployment policy.");
        }

        DnaXRemoteSurfaceState current = await GetStoredSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        EnsureDeploymentBinding(current);
        EnsureVersion(surface, current.Version, expectedVersion);
        DateTimeOffset revokedAt = _clock.GetUtcNow();
        if (!await _store.TryRevokeCredentialAsync(
            surface,
            DeploymentId,
            expectedVersion,
            revokedAt,
            cancellationToken).ConfigureAwait(false))
        {
            throw ConcurrentChange(surface);
        }

        return new DnaXRevokedCredential(surface, revokedAt, checked(expectedVersion + 1));
    }

    public async ValueTask<DnaXRemoteEffectiveSurface> RotateEndpointAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteSurfaceOptions policy = Policy(surface);
        EnsureFeatureAvailable(surface, policy);
        if (!policy.UseRandomizedEndpoint)
        {
            throw new DnaXRemotePolicyException($"{surface} uses its fixed endpoint and cannot be rotated.");
        }

        if (!policy.AllowEndpointRotation)
        {
            throw new DnaXRemotePolicyException($"Endpoint rotation is forbidden for {surface} by deployment policy.");
        }

        DnaXRemoteSurfaceState current = await GetStoredSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        EnsureDeploymentBinding(current);
        EnsureVersion(surface, current.Version, expectedVersion);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            string segment = CreateRouteSegment(surface);
            if (await CollidesAsync(surface, segment, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            DnaXRemoteSurfaceState replacement = current with
            {
                ProtectedRoute = _routeProtector.Protect(segment),
                RouteSuffix = segment[^8..],
                Version = checked(expectedVersion + 1),
                UpdatedAtUtc = _clock.GetUtcNow(),
            };
            if (await _store.TryReplaceSurfaceAsync(replacement, expectedVersion, cancellationToken)
                .ConfigureAwait(false))
            {
                return await GetSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
            }

            throw ConcurrentChange(surface);
        }

        throw new InvalidOperationException($"Could not generate a collision-free endpoint for {surface}.");
    }

    public async ValueTask<DnaXRemoteEffectiveSurface> SetActivationAsync(
        DnaXRemoteSurface surface,
        bool active,
        bool allowAnonymous,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteSurfaceOptions policy = Policy(surface);
        EnsureFeatureAvailable(surface, policy);
        if (!policy.AllowRuntimeActivation)
        {
            throw new DnaXRemotePolicyException($"Runtime activation is forbidden for {surface} by deployment policy.");
        }

        if (allowAnonymous && surface != DnaXRemoteSurface.Mcp)
        {
            throw new DnaXRemotePolicyException("Anonymous access is supported only for MCP.");
        }

        if (allowAnonymous && !policy.AllowAnonymous)
        {
            throw new DnaXRemotePolicyException("Anonymous MCP is forbidden by deployment policy.");
        }

        DnaXRemoteSurfaceState current = await GetStoredSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        EnsureDeploymentBinding(current);
        EnsureVersion(surface, current.Version, expectedVersion);
        DnaXRemoteStoredCredential? credential = await _store.GetCredentialAsync(surface, cancellationToken)
            .ConfigureAwait(false);
        if (active && credential is null && !allowAnonymous)
        {
            throw new DnaXRemotePolicyException($"Generate a {surface} credential before activation.");
        }

        string? protectedRoute = current.ProtectedRoute;
        string? routeSuffix = current.RouteSuffix;
        if (active && policy.UseRandomizedEndpoint && protectedRoute is null)
        {
            string segment = CreateRouteSegment(surface);
            if (await CollidesAsync(surface, segment, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The generated route collided with an existing route. Rotate and retry.");
            }

            protectedRoute = _routeProtector.Protect(segment);
            routeSuffix = segment[^8..];
        }

        DnaXRemoteSurfaceState replacement = current with
        {
            IsActive = active,
            AllowAnonymous = active && allowAnonymous,
            ProtectedRoute = protectedRoute,
            RouteSuffix = routeSuffix,
            Version = checked(expectedVersion + 1),
            UpdatedAtUtc = _clock.GetUtcNow(),
        };
        if (!await _store.TryReplaceSurfaceAsync(replacement, expectedVersion, cancellationToken)
            .ConfigureAwait(false))
        {
            throw ConcurrentChange(surface);
        }

        return await GetSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<DnaXRemoteAuditRecord>> GetRecentActivityAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return _store.GetRecentAuditAsync(maximumCount, cancellationToken);
    }

    public async ValueTask<DnaXRemoteEffectiveSurface> GetSurfaceAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default)
    {
        DnaXRemoteSurfaceOptions policy = Policy(surface);
        DnaXRemoteSurfaceState state = await GetStoredSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        DnaXRemoteStoredCredential? storedCredential = await _store.GetCredentialAsync(surface, cancellationToken)
            .ConfigureAwait(false);
        DnaXRemoteCredentialMetadata? credential = storedCredential is null
            ? null
            : new DnaXRemoteCredentialMetadata(
                storedCredential.Id,
                storedCredential.Suffix,
                storedCredential.Scopes,
                storedCredential.CreatedAtUtc,
                storedCredential.ExpiresAtUtc);

        string? endpoint = EndpointPath(surface, policy, state, out string? routeError);
        DnaXRemoteAvailability availability;
        string? detail = null;
        if (!_options.Enabled)
        {
            availability = DnaXRemoteAvailability.Disabled;
        }
        else if (!policy.Available || !policy.AllowRuntimeActivation)
        {
            availability = DnaXRemoteAvailability.UnavailableByDeploymentPolicy;
        }
        else if (state.Version > 0 && !string.Equals(state.DeploymentId, DeploymentId, StringComparison.Ordinal))
        {
            availability = DnaXRemoteAvailability.UnavailableByDeploymentPolicy;
            detail = "Persisted state belongs to another deployment.";
        }
        else if (!state.IsActive)
        {
            availability = DnaXRemoteAvailability.Inactive;
        }
        else if (routeError is not null)
        {
            availability = DnaXRemoteAvailability.Misconfigured;
            detail = routeError;
        }
        else if (state.AllowAnonymous && (surface != DnaXRemoteSurface.Mcp || !policy.AllowAnonymous))
        {
            availability = DnaXRemoteAvailability.Misconfigured;
            detail = "Persisted anonymous access is forbidden by current deployment policy.";
        }
        else if (!state.AllowAnonymous && credential is null)
        {
            availability = DnaXRemoteAvailability.Misconfigured;
            detail = "The active surface has no credential.";
        }
        else
        {
            availability = DnaXRemoteAvailability.Available;
        }

        return new DnaXRemoteEffectiveSurface(
            surface,
            availability,
            state.IsActive,
            state.AllowAnonymous && policy.AllowAnonymous,
            policy.UseRandomizedEndpoint ? DnaXRemoteRouteMode.Randomized : DnaXRemoteRouteMode.Fixed,
            endpoint,
            state.RouteSuffix,
            credential,
            state.Version,
            detail);
    }

    public async ValueTask<DnaXRemoteAuthentication> AuthenticateAsync(
        DnaXRemoteEffectiveSurface effective,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        if (!effective.IsAvailable)
        {
            return new DnaXRemoteAuthentication(false, null, null, []);
        }

        if (effective.IsAnonymous)
        {
            return new DnaXRemoteAuthentication(true, "anonymous-mcp", null, []);
        }

        string? token = ParseBearer(authorization);
        if (token is null)
        {
            return new DnaXRemoteAuthentication(false, null, null, []);
        }

        DnaXRemoteStoredCredential? credential = await _store.GetCredentialAsync(
            effective.Surface,
            cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.Algorithm != HashAlgorithm ||
            credential.ExpiresAtUtc is not null && credential.ExpiresAtUtc <= _clock.GetUtcNow())
        {
            return new DnaXRemoteAuthentication(false, null, null, []);
        }

        byte[] actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        if (actual.Length != credential.Hash.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, credential.Hash))
        {
            return new DnaXRemoteAuthentication(false, null, null, []);
        }

        return new DnaXRemoteAuthentication(
            true,
            $"{effective.Surface.ToString().ToLowerInvariant()}:{credential.Suffix}",
            credential.Suffix,
            credential.Scopes);
    }

    public async ValueTask RecordAuditAsync(
        DnaXRemoteAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Audit.Enabled)
        {
            return;
        }

        await _store.RecordAuditAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        await _store.PruneAuditAsync(
            _clock.GetUtcNow() - _options.Audit.Retention,
            _options.Audit.MaximumEvents,
            cancellationToken).ConfigureAwait(false);
    }

    private DnaXRemoteSurfaceOptions Policy(DnaXRemoteSurface surface) =>
        surface == DnaXRemoteSurface.Api ? _options.Api : _options.Mcp;

    private void EnsureFeatureAvailable(DnaXRemoteSurface surface, DnaXRemoteSurfaceOptions policy)
    {
        if (!_options.Enabled || !policy.Available)
        {
            throw new DnaXRemotePolicyException($"{surface} is unavailable by deployment policy.");
        }
    }

    private async ValueTask<DnaXRemoteSurfaceState> GetStoredSurfaceAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken)
    {
        DnaXRemoteSurfaceState? state = await _store.GetSurfaceAsync(surface, cancellationToken)
            .ConfigureAwait(false);
        return state ?? new DnaXRemoteSurfaceState(
            surface,
            DeploymentId,
            IsActive: false,
            AllowAnonymous: false,
            ProtectedRoute: null,
            RouteSuffix: null,
            Version: 0,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);
    }

    private string? EndpointPath(
        DnaXRemoteSurface surface,
        DnaXRemoteSurfaceOptions policy,
        DnaXRemoteSurfaceState state,
        out string? error)
    {
        error = null;
        if (!policy.UseRandomizedEndpoint)
        {
            return policy.FixedEndpointPath;
        }

        if (state.ProtectedRoute is null)
        {
            if (state.IsActive)
            {
                error = "The randomized route is missing.";
            }

            return null;
        }

        try
        {
            string segment = _routeProtector.Unprotect(state.ProtectedRoute);
            return surface == DnaXRemoteSurface.Api
                ? $"/{segment}/api/v1"
                : $"/{segment}/mcp";
        }
        catch (CryptographicException)
        {
            error = "The randomized route cannot be unprotected with the current data-protection key ring.";
            return null;
        }
    }

    private async ValueTask<bool> CollidesAsync(
        DnaXRemoteSurface surface,
        string segment,
        CancellationToken cancellationToken)
    {
        DnaXRemoteSurface other = surface == DnaXRemoteSurface.Api
            ? DnaXRemoteSurface.Mcp
            : DnaXRemoteSurface.Api;
        DnaXRemoteSurfaceState otherState = await GetStoredSurfaceAsync(other, cancellationToken).ConfigureAwait(false);
        if (otherState.ProtectedRoute is null)
        {
            return false;
        }

        try
        {
            return string.Equals(_routeProtector.Unprotect(otherState.ProtectedRoute), segment, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    private static string CreateRouteSegment(DnaXRemoteSurface surface)
    {
        string prefix = surface == DnaXRemoteSurface.Api ? "dnax-api-" : "dnax-mcp-";
        return prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
    }

    private static string[] NormalizeScopes(IReadOnlyCollection<string>? scopes) => scopes is null
        ? []
        : scopes
            .Select(static scope => scope?.Trim() ?? string.Empty)
            .Where(static scope => scope.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? ParseBearer(string? value)
    {
        const string prefix = "Bearer ";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = value[prefix.Length..].Trim();
        return token.Length > 0 && !token.Any(char.IsWhiteSpace) && !token.Contains(',', StringComparison.Ordinal)
            ? token
            : null;
    }

    private static void EnsureVersion(DnaXRemoteSurface surface, long actual, long expected)
    {
        if (actual != expected)
        {
            throw ConcurrentChange(surface);
        }
    }

    private string DeploymentId => _options.DeploymentId?.Trim() ?? string.Empty;

    private void EnsureDeploymentBinding(DnaXRemoteSurfaceState state)
    {
        if (state.Version > 0 && !string.Equals(state.DeploymentId, DeploymentId, StringComparison.Ordinal))
        {
            throw new DnaXRemotePolicyException("Persisted remote-access state belongs to another deployment.");
        }
    }

    private static DnaXRemoteConcurrencyException ConcurrentChange(DnaXRemoteSurface surface) =>
        new($"{surface} remote-access state changed concurrently. Reload it and retry.");
}
