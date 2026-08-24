namespace DnaX.Data.Migrations.Sqlite;

public sealed class DnaXSqliteMigrationOptions
{
    public bool EnforceForeignKeys { get; set; } = true;

    public bool EnableWriteAheadLogging { get; set; } = true;

    public bool DeferForeignKeysDuringMigration { get; set; } = true;

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}
