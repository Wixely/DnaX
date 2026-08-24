using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.PostgreSql;

public static class DnaXPostgreSqlMigrationExtensions
{
    public static DnaXMigrationOptions UsePostgreSql(
        this DnaXMigrationOptions options,
        Action<DnaXPostgreSqlMigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        DnaXPostgreSqlMigrationOptions postgreSqlOptions = new();
        configure?.Invoke(postgreSqlOptions);
        options.Adapter = new DnaXPostgreSqlMigrationAdapter(postgreSqlOptions);
        return options;
    }
}
