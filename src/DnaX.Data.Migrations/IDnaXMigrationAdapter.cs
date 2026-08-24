using System.Data.Common;

namespace DnaX.Data.Migrations;

public interface IDnaXMigrationAdapter
{
    string ProviderName { get; }

    DnaXMigrationAtomicity Atomicity { get; }

    ValueTask InitializeConnectionAsync(DbConnection connection, CancellationToken cancellationToken);

    ValueTask<IDnaXMigrationSession> AcquireSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken);

    ValueTask EnsureLedgerAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DnaXAppliedMigration>> ReadLedgerAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken);

    ValueTask RecordAppliedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        DnaXAppliedMigration migration,
        CancellationToken cancellationToken);

    ValueTask<string> InspectSchemaAsync(DbConnection connection, CancellationToken cancellationToken);
}

public interface IDnaXMigrationSession : IAsyncDisposable
{
    DbTransaction? Transaction { get; }

    ValueTask CheckpointAsync(CancellationToken cancellationToken);

    ValueTask CommitAsync(CancellationToken cancellationToken);

    ValueTask RollbackAsync(CancellationToken cancellationToken);
}

public interface IDnaXDatabaseMigrator
{
    ValueTask<DnaXMigrationStatus> GetStatusAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXMigrationResult> MigrateAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<DnaXMigrationBaselineResult> BaselineAsync(
        string name,
        int throughVersion,
        Func<DnaXBaselineVerificationContext, CancellationToken, ValueTask> verifyExistingSchema,
        CancellationToken cancellationToken = default);
}
