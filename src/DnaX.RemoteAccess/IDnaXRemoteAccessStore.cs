namespace DnaX.RemoteAccess;

public interface IDnaXRemoteAccessStore
{
    ValueTask<DnaXRemoteSurfaceState?> GetSurfaceAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXRemoteStoredCredential?> GetCredentialAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryReplaceSurfaceAsync(
        DnaXRemoteSurfaceState state,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryReplaceCredentialAsync(
        DnaXRemoteStoredCredential credential,
        string deploymentId,
        long expectedSurfaceVersion,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryRevokeCredentialAsync(
        DnaXRemoteSurface surface,
        string deploymentId,
        long expectedSurfaceVersion,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask RecordAuditAsync(
        DnaXRemoteAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DnaXRemoteAuditRecord>> GetRecentAuditAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask PruneAuditAsync(
        DateTimeOffset retainAfterUtc,
        int maximumEvents,
        CancellationToken cancellationToken = default);
}

public interface IDnaXRemoteAccessAdministration
{
    ValueTask<DnaXRemoteAdministrationState> GetStateAsync(CancellationToken cancellationToken = default);

    ValueTask<DnaXGeneratedCredential> RotateCredentialAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        IReadOnlyCollection<string>? scopes = null,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXRevokedCredential> RevokeCredentialAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXRemoteEffectiveSurface> RotateEndpointAsync(
        DnaXRemoteSurface surface,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXRemoteEffectiveSurface> SetActivationAsync(
        DnaXRemoteSurface surface,
        bool active,
        bool allowAnonymous,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DnaXRemoteAuditRecord>> GetRecentActivityAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public interface IDnaXRemoteAccessDiagnostics
{
    ValueTask<DnaXRemoteDiagnosticState> GetDiagnosticStateAsync(
        CancellationToken cancellationToken = default);
}
