namespace DnaX.Data.Migrations.SqlServer;

public sealed class DnaXSqlServerMigrationOptions
{
    public string Schema { get; set; } = "dbo";

    public string LedgerTable { get; set; } = "__DnaXMigrations";

    public bool CreateSchemaIfMissing { get; set; }

    public string LockResource { get; set; } = "DnaX.Data.Migrations";

    public string LockDatabasePrincipal { get; set; } = "public";

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
