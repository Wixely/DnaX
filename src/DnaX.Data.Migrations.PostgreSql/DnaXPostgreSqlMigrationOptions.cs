namespace DnaX.Data.Migrations.PostgreSql;

public sealed class DnaXPostgreSqlMigrationOptions
{
    public string Schema { get; set; } = "public";

    public string LedgerTable { get; set; } = "__DnaXMigrations";

    public bool CreateSchemaIfMissing { get; set; }

    public long AdvisoryLockKey { get; set; } = 0x444E41584D494752;

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}
