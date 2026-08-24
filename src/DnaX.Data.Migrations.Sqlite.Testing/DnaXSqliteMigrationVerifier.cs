using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.Sqlite.Testing;

public sealed record DnaXHistoricalMigrationResult(
    int InitialVersion,
    int AppliedMigrationCount,
    string SchemaSnapshot);

public sealed record DnaXHistoricalMigrationVerification(
    string CanonicalSchemaSnapshot,
    IReadOnlyList<DnaXHistoricalMigrationResult> HistoricalVersions);

public static class DnaXSqliteMigrationVerifier
{
    public static async ValueTask<DnaXHistoricalMigrationVerification> VerifyAllHistoricalVersionsAsync(
        DnaXMigrationManifest manifest,
        Action<DnaXSqliteMigrationTestOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await using DnaXSqliteMigrationTestScope canonical = await DnaXSqliteMigrationTestScope
            .CreateAsync(manifest, configure, cancellationToken)
            .ConfigureAwait(false);
        string canonicalSnapshot = await canonical.InspectSchemaAsync(cancellationToken).ConfigureAwait(false);
        List<DnaXHistoricalMigrationResult> results = [];

        for (int version = 0; version < manifest.CurrentVersion; version++)
        {
            await using DnaXSqliteMigrationTestScope historical = await DnaXSqliteMigrationTestScope
                .CreateHistoricalAsync(manifest, version, configure, cancellationToken)
                .ConfigureAwait(false);
            DnaXMigrationResult upgrade = await historical.UpgradeAsync(cancellationToken).ConfigureAwait(false);
            string snapshot = await historical.InspectSchemaAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot, canonicalSnapshot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Upgrading from schema version {version} did not produce the canonical schema snapshot.");
            }

            results.Add(new(version, upgrade.AppliedMigrations.Count, snapshot));
        }

        return new(canonicalSnapshot, results.AsReadOnly());
    }
}
