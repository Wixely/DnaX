namespace DnaX.Data.Migrations.Oracle;

public sealed class DnaXOracleMigrationOptions
{
    public string? Schema { get; set; }

    public string LedgerTable { get; set; } = "DNAX_MIGRATIONS";

    public int LockId { get; set; } = 444_269_984;

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);
}
