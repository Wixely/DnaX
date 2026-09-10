using System.Security.Cryptography;
using System.Text;
using DnaX.Data.Migrations.Sqlite;
using DnaX.RemoteAccess.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Migrations.Tests;

/// <summary>
/// Covers issue #1: migration checksums must not depend on the line endings a checkout produced,
/// and ledgers written before normalization must remain readable.
/// </summary>
public sealed class DnaXChecksumLineEndingTests : IDisposable
{
    /// <summary>
    /// Normalized in code rather than trusted from the file: this literal's line endings depend on
    /// the checkout, which is precisely the variable under test.
    /// </summary>
    private static readonly string LfSql = """
        CREATE TABLE Records (
            Id TEXT NOT NULL PRIMARY KEY
        );
        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string CrlfSql => ToCrlf(LfSql);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnax-checksum-tests",
        Guid.NewGuid().ToString("N"));

    public DnaXChecksumLineEndingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ChecksumIsStableAcrossLineEndings()
    {
        Assert.NotEqual(LfSql, CrlfSql);
        Assert.Equal(DnaXMigration.ComputeChecksum(LfSql), DnaXMigration.ComputeChecksum(CrlfSql));

        // The reporter's probe from issue #1, pinned so the normalized value cannot drift silently.
        Assert.Equal(
            "sha256:55d364f845aee5162674e52a44f20a911f67064f77af8389d5f9cddc18cb312e",
            DnaXMigration.ComputeChecksum("CREATE TABLE Probe (\n    Id TEXT NOT NULL\n);\n"));
        Assert.Equal(
            "sha256:55d364f845aee5162674e52a44f20a911f67064f77af8389d5f9cddc18cb312e",
            DnaXMigration.ComputeChecksum("CREATE TABLE Probe (\r\n    Id TEXT NOT NULL\r\n);\r\n"));
    }

    [Fact]
    public void ChecksumStillDistinguishesContentChanges()
    {
        Assert.NotEqual(
            DnaXMigration.ComputeChecksum(LfSql),
            DnaXMigration.ComputeChecksum(LfSql.Replace("Records", "Rows", StringComparison.Ordinal)));

        // Normalization must not extend to whitespace the author actually typed.
        Assert.NotEqual(
            DnaXMigration.ComputeChecksum("SELECT 1;"),
            DnaXMigration.ComputeChecksum("SELECT 1; "));
        Assert.NotEqual(
            DnaXMigration.ComputeChecksum("SELECT 1;"),
            DnaXMigration.ComputeChecksum("SELECT  1;"));
    }

    [Fact]
    public void CompatibleChecksumsCoverBothPlatformsRegardlessOfBuildCheckout()
    {
        // The point of the fix: an LF build must still recognize a CRLF-written ledger, which
        // requires synthesizing the variant it never sees.
        string[] fromLf = DnaXMigration.ComputeCompatibleChecksums(LfSql);
        string[] fromCrlf = DnaXMigration.ComputeCompatibleChecksums(CrlfSql);

        Assert.Equal(DnaXMigration.ComputeChecksum(LfSql), fromLf[0]);
        Assert.Contains(LegacyChecksum(LfSql), fromLf, StringComparer.Ordinal);
        Assert.Contains(LegacyChecksum(CrlfSql), fromLf, StringComparer.Ordinal);
        Assert.Equal(fromLf.Order(StringComparer.Ordinal), fromCrlf.Order(StringComparer.Ordinal));
        Assert.Equal(fromLf.Length, fromLf.Distinct(StringComparer.Ordinal).Count());

        // Single-line content has nothing to normalize, so no compatibility entries are added.
        Assert.Single(DnaXMigration.ComputeCompatibleChecksums("SELECT 1;"));
    }

    [Fact]
    public void EmbeddedSqlPreservesCompatibleChecksums()
    {
        // Guards the reconstruction in EmbeddedSql that attaches Source: dropping the set there
        // compiles cleanly and silently leaves every embedded-SQL consumer broken.
        DnaXMigration embedded = DnaXMigration.EmbeddedSql(
            1,
            "embedded",
            "Embedded",
            typeof(DnaXChecksumLineEndingTests).Assembly,
            "DnaX.Data.Migrations.Tests.Resources.embedded-migration.sql");

        Assert.Equal("DnaX.Data.Migrations.Tests.Resources.embedded-migration.sql", embedded.Source);
        Assert.True(embedded.CompatibleChecksums.Count > 1);
        Assert.Equal(embedded.Checksum, embedded.CompatibleChecksums[0]);
        Assert.True(embedded.MatchesRecordedChecksum(LegacyChecksum(ToCrlf(EmbeddedResourceText()))));
    }

    [Fact]
    public void LedgerWrittenOnEitherPlatformIsAcceptedByABuildFromTheOther()
    {
        // Table-driven over {build checkout} x {ledger checkout} - the exact shape of the bug.
        foreach (string build in new[] { LfSql, CrlfSql })
        {
            DnaXMigration migration = DnaXMigration.Sql(1, "records", "Create records", build);
            DnaXMigrationManifest manifest = new(1, [migration]);

            foreach (string ledgerSource in new[] { LfSql, CrlfSql })
            {
                DnaXMigrationStatus status = DnaXDatabaseMigrator.Evaluate(
                    manifest,
                    [Applied(1, "records", "Create records", LegacyChecksum(ledgerSource))]);

                Assert.Equal(DnaXMigrationState.Current, status.State);
                Assert.Empty(status.ChecksumDrifts);
            }
        }
    }

    [Fact]
    public void GenuineContentChangeStillDriftsAndReportsBothChecksums()
    {
        DnaXMigrationManifest manifest = new(
            1,
            [DnaXMigration.Sql(1, "records", "Create records", LfSql)]);
        string recorded = LegacyChecksum(LfSql.Replace("Records", "Rows", StringComparison.Ordinal));

        DnaXMigrationStatus status = DnaXDatabaseMigrator.Evaluate(
            manifest,
            [Applied(1, "records", "Create records", recorded)]);

        Assert.Equal(DnaXMigrationState.Drifted, status.State);
        DnaXChecksumDrift drift = Assert.Single(status.ChecksumDrifts);
        Assert.Equal(1, drift.Version);
        Assert.Equal("records", drift.Id);
        Assert.Equal(manifest.Migrations[0].Checksum, drift.ExpectedChecksum);
        Assert.Equal(recorded, drift.RecordedChecksum);

        string issue = Assert.Single(status.Issues);
        Assert.Contains(manifest.Migrations[0].Checksum[..23], issue, StringComparison.Ordinal);
        Assert.Contains(recorded[..23], issue, StringComparison.Ordinal);
    }

    [Fact]
    public void PositionalStatusConstructionStillCompilesAndDriftsDefaultToEmpty()
    {
        // Stands in for the API-compat check the repo has no baseline tooling for.
        DnaXMigrationStatus status = new(
            DnaXMigrationState.Pending,
            2,
            1,
            Array.Empty<DnaXMigration>(),
            Array.Empty<string>());

        Assert.Empty(status.ChecksumDrifts);
        Assert.Empty((status with { State = DnaXMigrationState.Current }).ChecksumDrifts);
    }

    [Fact]
    public async Task LegacyLedgerIsAcceptedWithoutBeingRewritten()
    {
        string path = Path.Combine(_directory, "legacy.db");
        DnaXMigrationManifest manifest = new(
            1,
            [DnaXMigration.Sql(1, "records", "Create records", LfSql)]);

        await using ServiceProvider provider = CreateProvider(path, manifest);
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        await migrator.MigrateAsync("Primary");

        // Plant the checksum a pre-normalization Windows build would have written.
        string legacy = LegacyChecksum(CrlfSql);
        Assert.NotEqual(manifest.Migrations[0].Checksum, legacy);
        await ExecuteAsync(path, "UPDATE \"__DnaXMigrations\" SET \"Checksum\" = $checksum;", ("$checksum", legacy));

        DnaXMigrationStatus status = await migrator.GetStatusAsync("Primary");
        Assert.Equal(DnaXMigrationState.Current, status.State);
        Assert.Empty(status.ChecksumDrifts);
        await migrator.MigrateAsync("Primary");

        // No auto-heal: the applied row is left exactly as recorded.
        Assert.Equal(legacy, await ReadChecksumAsync(path));
    }

    [Fact]
    public async Task NewMigrationRecordsTheNormalizedChecksum()
    {
        string path = Path.Combine(_directory, "normalized.db");
        DnaXMigrationManifest manifest = new(
            1,
            [DnaXMigration.Sql(1, "records", "Create records", CrlfSql)]);

        await using ServiceProvider provider = CreateProvider(path, manifest);
        await provider.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Primary");

        Assert.Equal(DnaXMigration.ComputeChecksum(LfSql), await ReadChecksumAsync(path));
    }

    [Fact]
    public void RemoteAccessSchemaChecksumIsPinned()
    {
        // The shipped manifest whose ledger identity must be identical on every CI leg. This is
        // the assertion that would have caught the original bug.
        Assert.Equal(
            "sha256:9313b7cefb928445f233cb78379e4b3cb4ac4b85c124683e958b9c3f19389f11",
            DnaXRemoteAccessSqliteSchema.Manifest.Migrations[0].Checksum);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string ToCrlf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>
    /// Hashes content verbatim, reproducing what a build recorded before normalization.
    /// ComputeChecksum cannot be used here because it now normalizes.
    /// </summary>
    private static string LegacyChecksum(string content) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";

    private static string EmbeddedResourceText()
    {
        using Stream stream = typeof(DnaXChecksumLineEndingTests).Assembly.GetManifestResourceStream(
            "DnaX.Data.Migrations.Tests.Resources.embedded-migration.sql")!;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DnaXAppliedMigration Applied(int version, string id, string name, string checksum) =>
        new(version, id, name, checksum, null, DateTimeOffset.UnixEpoch);

    private static ServiceProvider CreateProvider(string path, DnaXMigrationManifest manifest)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDnaXDataMigrations("Primary", options =>
        {
            options.ConnectionFactory = _ => new SqliteConnection($"Data Source={path};Pooling=False");
            options.Manifest = manifest;
            options.UseSqlite();
        });
        return services.BuildServiceProvider();
    }

    private static async Task ExecuteAsync(string path, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadChecksumAsync(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"Checksum\" FROM \"__DnaXMigrations\" WHERE \"Version\" = 1;";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
