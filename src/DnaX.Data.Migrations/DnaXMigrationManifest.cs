namespace DnaX.Data.Migrations;

public sealed class DnaXMigrationManifest
{
    public DnaXMigrationManifest(
        int currentVersion,
        IEnumerable<DnaXMigration> migrations,
        string? canonicalSchema = null)
    {
        if (currentVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentVersion));
        }

        ArgumentNullException.ThrowIfNull(migrations);
        DnaXMigration[] ordered = migrations.ToArray();
        HashSet<string> ids = new(StringComparer.Ordinal);

        for (int index = 0; index < ordered.Length; index++)
        {
            DnaXMigration migration = ordered[index]
                ?? throw new ArgumentException("A migration cannot be null.", nameof(migrations));
            int expectedVersion = index + 1;
            if (migration.Version != expectedVersion)
            {
                throw new ArgumentException(
                    $"Migration '{migration.Id}' is at position {expectedVersion} but declares version {migration.Version}. " +
                    "Manifests must be explicitly ordered and contiguous from version 1.",
                    nameof(migrations));
            }

            if (!ids.Add(migration.Id))
            {
                throw new ArgumentException($"Migration identifier '{migration.Id}' is duplicated.", nameof(migrations));
            }
        }

        if (ordered.Length != currentVersion)
        {
            throw new ArgumentException(
                $"The manifest declares current version {currentVersion} but contains {ordered.Length} migrations.",
                nameof(migrations));
        }

        CurrentVersion = currentVersion;
        Migrations = Array.AsReadOnly(ordered);
        CanonicalSchema = canonicalSchema;
    }

    public int CurrentVersion { get; }

    public IReadOnlyList<DnaXMigration> Migrations { get; }

    public string? CanonicalSchema { get; }
}
