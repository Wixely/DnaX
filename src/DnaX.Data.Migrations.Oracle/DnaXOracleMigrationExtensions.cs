using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.Oracle;

public static class DnaXOracleMigrationExtensions
{
    public static DnaXMigrationOptions UseOracle(
        this DnaXMigrationOptions options,
        Action<DnaXOracleMigrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        DnaXOracleMigrationOptions oracleOptions = new();
        configure?.Invoke(oracleOptions);
        options.Adapter = new DnaXOracleMigrationAdapter(oracleOptions);
        return options;
    }
}
