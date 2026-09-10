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
        string? source,
        IReadOnlyList<string>? compatibleChecksums = null)
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
        CompatibleChecksums = compatibleChecksums ?? [checksum];
    }

    public int Version { get; }

    public string Id { get; }

    public string Name { get; }

    public string Checksum { get; }

    public string? Source { get; }

    internal DnaXMigrationOperation Operation { get; }

    /// <summary>
    /// Checksums that identify this migration's content. <see cref="Checksum"/> is always the first
    /// entry and is the only value ever written to a ledger; the remainder are line-ending variants
    /// accepted from ledgers written by builds that hashed the content before normalization.
    /// </summary>
    internal IReadOnlyList<string> CompatibleChecksums { get; }

    /// <summary>
    /// Determines whether a checksum recorded in a ledger identifies this migration's content,
    /// accepting historical line-ending variants alongside the normalized value.
    /// </summary>
    internal bool MatchesRecordedChecksum(string recorded)
    {
        for (int index = 0; index < CompatibleChecksums.Count; index++)
        {
            if (string.Equals(CompatibleChecksums[index], recorded, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
            source: null,
            ComputeCompatibleChecksums(sql));
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
            resourceName,
            migration.CompatibleChecksums);
    }

    public static DnaXMigration Code(
        int version,
        string id,
        string name,
        string checksum,
        DnaXMigrationOperation operation) =>
        new(version, id, name, NormalizeChecksum(checksum), operation, source: null);

    /// <summary>
    /// Computes the checksum of migration content. Windows line endings are normalized to
    /// <c>\n</c> before hashing, so the same content checksums identically regardless of the
    /// line endings a checkout produced. No other normalization is applied.
    /// </summary>
    public static string ComputeChecksum(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Hash(NormalizeLineEndings(content));
    }

    private static string Hash(string content)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"sha256:{Convert.ToHexStringLower(digest)}";
    }

    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Computes every checksum that identifies <paramref name="content"/>: the normalized value
    /// first, then the line-ending variants a pre-normalization build could have recorded. The
    /// carriage-return variant is deliberately synthesized rather than observed, because a build
    /// only ever sees the one variant its own checkout produced and would otherwise be unable to
    /// recognize a ledger written on the other platform.
    /// </summary>
    internal static string[] ComputeCompatibleChecksums(string content)
    {
        string normalized = NormalizeLineEndings(content);
        string normalizedChecksum = Hash(normalized);
        List<string> checksums = [normalizedChecksum];

        foreach (string variant in new[] { normalized.Replace("\n", "\r\n", StringComparison.Ordinal), content })
        {
            string checksum = Hash(variant);
            if (!checksums.Contains(checksum, StringComparer.Ordinal))
            {
                checksums.Add(checksum);
            }
        }

        return [.. checksums];
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
