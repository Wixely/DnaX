using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.Sqlite;

public static class DnaXSqliteMigrationExtensions
{
    public static DnaXMigrationOptions UseSqlite(
        this DnaXMigrationOptions options,
        Action<DnaXSqliteMigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        DnaXSqliteMigrationOptions sqliteOptions = new();
        configure?.Invoke(sqliteOptions);
        options.Adapter = new DnaXSqliteMigrationAdapter(sqliteOptions);
        return options;
    }
}
