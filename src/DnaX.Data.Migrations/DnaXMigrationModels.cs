namespace DnaX.Data.Migrations;

public enum DnaXMigrationState
{
    Current,
    Pending,
    Drifted,
    Failed,
    UnsupportedFuture,
}

public enum DnaXMigrationAtomicity
{
    AtomicChain,
    ProviderManagedCheckpoints,
}

public sealed record DnaXAppliedMigration(
    int Version,
    string Id,
    string Name,
    string Checksum,
    string? ApplicationVersion,
    DateTimeOffset AppliedAtUtc);

public sealed record DnaXMigrationStatus(
    DnaXMigrationState State,
    int CurrentVersion,
    int DatabaseVersion,
    IReadOnlyList<DnaXMigration> PendingMigrations,
    IReadOnlyList<string> Issues)
{
    public bool CanMigrate => State is DnaXMigrationState.Current or DnaXMigrationState.Pending;
}

public sealed record DnaXMigrationResult(
    string DatabaseName,
    DnaXMigrationStatus Status,
    IReadOnlyList<DnaXMigration> AppliedMigrations,
    TimeSpan Duration,
    DnaXMigrationAtomicity Atomicity);

public sealed record DnaXBeforeMigrationContext(
    string DatabaseName,
    DnaXMigrationManifest Manifest,
    DnaXMigrationStatus Status,
    System.Data.Common.DbConnection Connection,
    System.Data.Common.DbTransaction? Transaction);

public sealed record DnaXBaselineVerificationContext(
    string DatabaseName,
    int ThroughVersion,
    DnaXMigrationManifest Manifest,
    System.Data.Common.DbConnection Connection,
    System.Data.Common.DbTransaction? Transaction);

public sealed record DnaXMigrationBaselineResult(
    string DatabaseName,
    int BaselineVersion,
    DnaXMigrationStatus Status,
    TimeSpan Duration,
    DnaXMigrationAtomicity Atomicity);
