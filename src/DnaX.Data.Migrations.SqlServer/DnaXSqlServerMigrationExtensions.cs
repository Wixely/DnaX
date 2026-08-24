using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.SqlServer;

public static class DnaXSqlServerMigrationExtensions
{
    public static DnaXMigrationOptions UseSqlServer(
        this DnaXMigrationOptions options,
        Action<DnaXSqlServerMigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        DnaXSqlServerMigrationOptions sqlServerOptions = new();
        configure?.Invoke(sqlServerOptions);
        options.Adapter = new DnaXSqlServerMigrationAdapter(sqlServerOptions);
        return options;
    }
}
