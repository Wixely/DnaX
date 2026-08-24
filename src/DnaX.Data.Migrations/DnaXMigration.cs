using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DnaX.Data.Migrations;

public delegate ValueTask DnaXMigrationOperation(
    DbConnection connection,
    DbTransaction? transaction,
    CancellationToken cancellationToken);

public sealed class DnaXMigration
{
    private DnaXMigration(
        int version,
        string id,
        string name,
        string checksum,
        DnaXMigrationOperation operation,
        string? source)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Migration versions start at 1.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        ArgumentNullException.ThrowIfNull(operation);

        Version = version;
        Id = id;
        Name = name;
        Checksum = checksum;
        Operation = operation;
        Source = source;
    }

    public int Version { get; }

    public string Id { get; }

    public string Name { get; }

    public string Checksum { get; }

    public string? Source { get; }

    internal DnaXMigrationOperation Operation { get; }

    public static DnaXMigration Sql(int version, string id, string name, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return new(
            version,
            id,
            name,
            ComputeChecksum(sql),
            (connection, transaction, cancellationToken) =>
                ExecuteSqlAsync(connection, transaction, sql, cancellationToken),
            source: null);
    }

    public static DnaXMigration EmbeddedSql(
        int version,
        string id,
        string name,
        Assembly assembly,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded migration resource '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string sql = reader.ReadToEnd();
        DnaXMigration migration = Sql(version, id, name, sql);
        return new(
            migration.Version,
            migration.Id,
            migration.Name,
            migration.Checksum,
            migration.Operation,
            resourceName);
    }

    public static DnaXMigration Code(
        int version,
        string id,
        string name,
        string checksum,
        DnaXMigrationOperation operation) =>
        new(version, id, name, NormalizeChecksum(checksum), operation, source: null);

    public static string ComputeChecksum(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"sha256:{Convert.ToHexStringLower(digest)}";
    }

    private static string NormalizeChecksum(string checksum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
        string normalized = checksum.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal) || normalized.Length != 71)
        {
            throw new ArgumentException(
                "Code migration checksums must use the form 'sha256:' followed by 64 hexadecimal characters.",
                nameof(checksum));
        }

        _ = Convert.FromHexString(normalized[7..]);
        return normalized;
    }

    private static async ValueTask ExecuteSqlAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
