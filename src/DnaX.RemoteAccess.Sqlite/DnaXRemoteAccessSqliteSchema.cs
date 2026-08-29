using DnaX.Data.Migrations;

namespace DnaX.RemoteAccess.Sqlite;

public static class DnaXRemoteAccessSqliteSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 1,
        migrations:
        [
            DnaXMigration.Sql(1, "create-remote-access", "Create remote-access state, credentials, and audit", """
                CREATE TABLE DnaXRemoteSurfaces (
                    Surface TEXT NOT NULL PRIMARY KEY,
                    DeploymentId TEXT NOT NULL,
                    IsActive INTEGER NOT NULL CHECK (IsActive IN (0, 1)),
                    AllowAnonymous INTEGER NOT NULL CHECK (AllowAnonymous IN (0, 1)),
                    ProtectedRoute TEXT NULL,
                    RouteSuffix TEXT NULL,
                    Version INTEGER NOT NULL CHECK (Version >= 0),
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE DnaXRemoteCredentials (
                    CredentialId TEXT NOT NULL PRIMARY KEY,
                    Surface TEXT NOT NULL,
                    Algorithm TEXT NOT NULL,
                    SecretHash BLOB NOT NULL,
                    Suffix TEXT NOT NULL,
                    ScopesJson TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ExpiresAtUtc TEXT NULL,
                    RevokedAtUtc TEXT NULL
                );

                CREATE UNIQUE INDEX IX_DnaXRemoteCredentials_ActiveSurface
                    ON DnaXRemoteCredentials (Surface)
                    WHERE RevokedAtUtc IS NULL;

                CREATE TABLE DnaXRemoteAuditEvents (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Surface TEXT NOT NULL,
                    ServiceIdentity TEXT NULL,
                    CredentialSuffix TEXT NULL,
                    Client TEXT NULL,
                    Method TEXT NOT NULL,
                    Action TEXT NOT NULL,
                    Result TEXT NOT NULL,
                    StatusCode INTEGER NOT NULL,
                    CorrelationId TEXT NOT NULL,
                    OccurredAtUtc TEXT NOT NULL
                );

                CREATE INDEX IX_DnaXRemoteAuditEvents_OccurredAtUtc
                    ON DnaXRemoteAuditEvents (OccurredAtUtc, Id);
                """)
        ]);
}
