namespace DnaX.Data.Migrations;

public class DnaXMigrationException : InvalidOperationException
{
    public DnaXMigrationException(string databaseName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }
}

public sealed class DnaXMigrationValidationException : DnaXMigrationException
{
    public DnaXMigrationValidationException(string databaseName, DnaXMigrationStatus status)
        : base(databaseName, BuildMessage(databaseName, status))
    {
        Status = status;
    }

    public DnaXMigrationStatus Status { get; }

    private static string BuildMessage(string databaseName, DnaXMigrationStatus status) =>
        $"Database '{databaseName}' is in migration state '{status.State}': {string.Join(" ", status.Issues)}";
}

public sealed class DnaXMigrationFailedException : DnaXMigrationException
{
    public DnaXMigrationFailedException(string databaseName, DnaXMigration migration, Exception innerException)
        : base(
            databaseName,
            $"Migration {migration.Version} ('{migration.Id}', {migration.Name}) failed for database '{databaseName}'.",
            innerException)
    {
        Migration = migration;
    }

    public DnaXMigration Migration { get; }
}

public sealed class DnaXMigrationDatabaseNotFoundException : DnaXMigrationException
{
    public DnaXMigrationDatabaseNotFoundException(string name, IEnumerable<string> registeredNames)
        : base(name, $"Migration database '{name}' is not registered. Registered databases: {string.Join(", ", registeredNames)}.")
    {
    }
}
