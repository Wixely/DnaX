using System.Data.Common;
using System.Globalization;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using DnaX.Data.Migrations.Sqlite.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Migrations.Tests;

public sealed class DnaXMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "dnax-migration-tests",
        Guid.NewGuid().ToString("N"));

    public DnaXMigrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ManifestRequiresExplicitContiguousOrderAndUniqueIds()
    {
        DnaXMigration second = DnaXMigration.Sql(2, "second", "Second", "SELECT 2;");
        DnaXMigration first = DnaXMigration.Sql(1, "first", "First", "SELECT 1;");

        ArgumentException reordered = Assert.Throws<ArgumentException>(
            () => new DnaXMigrationManifest(2, [second, first]));
        Assert.Contains("explicitly ordered", reordered.Message, StringComparison.Ordinal);

        ArgumentException duplicate = Assert.Throws<ArgumentException>(
            () => new DnaXMigrationManifest(
                2,
                [first, DnaXMigration.Sql(2, "first", "Duplicate", "SELECT 2;")]));
        Assert.Contains("duplicated", duplicate.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new DnaXMigrationManifest(2, [first]));
    }

    [Fact]
    public async Task FreshDatabaseMigratesAndSecondRunIsIdempotent()
    {
        DnaXMigrationManifest manifest = CreateManifest();
        await using ServiceProvider provider = CreateProvider(DatabasePath(), manifest, applicationVersion: "2.4.0");
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();

        DnaXMigrationResult first = await migrator.MigrateAsync("Primary");
        DnaXMigrationResult second = await migrator.MigrateAsync("Primary");

        Assert.Equal(2, first.AppliedMigrations.Count);
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(DnaXMigrationState.Current, second.Status.State);

        await using SqliteConnection connection = await OpenAsync(DatabasePath());
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), MAX(\"Version\"), MIN(\"ApplicationVersion\") FROM \"__DnaXMigrations\";";
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
        Assert.Equal("2.4.0", reader.GetString(2));
    }

    [Fact]
    public async Task AppliedContentChangeIsReportedAsDriftAndCannotMigrate()
    {
        string path = DatabasePath();
        await using (ServiceProvider initial = CreateProvider(path, CreateManifest()))
        {
            await initial.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Primary");
        }

        DnaXMigrationManifest changed = new(
            2,
            [
                DnaXMigration.Sql(1, "create-widgets", "Create widgets", "CREATE TABLE Widgets (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Changed INTEGER NULL);"),
                DnaXMigration.Sql(2, "widget-name-index", "Index widget names", "CREATE UNIQUE INDEX UX_Widgets_Name ON Widgets(Name);")
            ]);
        await using ServiceProvider provider = CreateProvider(path, changed);
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();

        DnaXMigrationStatus status = await migrator.GetStatusAsync("Primary");
        DnaXMigrationValidationException exception = await Assert.ThrowsAsync<DnaXMigrationValidationException>(
            async () => await migrator.MigrateAsync("Primary"));

        Assert.Equal(DnaXMigrationState.Drifted, status.State);
        Assert.Equal(DnaXMigrationState.Drifted, exception.Status.State);
        Assert.Contains(status.Issues, issue => issue.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FutureAndGappedLedgersAreRejected()
    {
        string path = DatabasePath();
        DnaXMigrationManifest manifest = CreateManifest();
        await using ServiceProvider provider = CreateProvider(path, manifest);
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        await migrator.MigrateAsync("Primary");

        await ExecuteAsync(path, "DELETE FROM \"__DnaXMigrations\" WHERE \"Version\" = 1;");
        DnaXMigrationStatus gap = await migrator.GetStatusAsync("Primary");
        Assert.Equal(DnaXMigrationState.Drifted, gap.State);
        Assert.Contains(gap.Issues, issue => issue.Contains("missing version 1", StringComparison.Ordinal));

        await ExecuteAsync(
            path,
            """
            INSERT INTO "__DnaXMigrations"
                ("Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc")
            VALUES
                (999, 'future', 'Future', 'sha256:0000000000000000000000000000000000000000000000000000000000000000', NULL, @now);
            """,
            ("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        DnaXMigrationStatus future = await migrator.GetStatusAsync("Primary");
        Assert.Equal(DnaXMigrationState.UnsupportedFuture, future.State);
    }

    [Fact]
    public async Task FailingChainRollsBackSchemaAndLedgerAtomically()
    {
        string path = DatabasePath();
        DnaXMigrationManifest manifest = new(
            2,
            [
                DnaXMigration.Sql(1, "create-first", "Create first", "CREATE TABLE FirstTable (Id INTEGER PRIMARY KEY);"),
                DnaXMigration.Sql(2, "invalid", "Invalid SQL", "CREATE TABLE Broken (;")
            ]);
        await using ServiceProvider provider = CreateProvider(path, manifest);

        DnaXMigrationFailedException exception = await Assert.ThrowsAsync<DnaXMigrationFailedException>(
            async () => await provider.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Primary"));

        Assert.Equal(2, exception.Migration.Version);
        await using SqliteConnection connection = await OpenAsync(path);
        Assert.Equal(0L, await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('FirstTable', '__DnaXMigrations');"));
    }

    [Fact]
    public async Task ConcurrentRunnersApplyEachMigrationOnce()
    {
        string path = DatabasePath();
        DnaXMigrationManifest manifest = CreateManifest();
        await using ServiceProvider provider = CreateProvider(
            path,
            manifest,
            sqlite: options =>
            {
                options.LockTimeout = TimeSpan.FromSeconds(10);
                options.LockRetryDelay = TimeSpan.FromMilliseconds(10);
            });
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();

        Task<DnaXMigrationResult> first = migrator.MigrateAsync("Primary").AsTask();
        Task<DnaXMigrationResult> second = migrator.MigrateAsync("Primary").AsTask();
        DnaXMigrationResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(2, results.Sum(result => result.AppliedMigrations.Count));
        Assert.Contains(results, result => result.AppliedMigrations.Count == 0);
        await using SqliteConnection connection = await OpenAsync(path);
        Assert.Equal(2L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM \"__DnaXMigrations\";"));
    }

    [Fact]
    public async Task LockWaitHonorsCancellation()
    {
        string path = DatabasePath();
        await using ServiceProvider provider = CreateProvider(
            path,
            CreateManifest(),
            sqlite: options => options.EnableWriteAheadLogging = false);
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        await migrator.GetStatusAsync("Primary");

        await using SqliteConnection blocker = await OpenAsync(path);
        await using SqliteTransaction transaction = blocker.BeginTransaction(deferred: false);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await migrator.MigrateAsync("Primary", cancellation.Token));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ForeignKeysAndSanitizedSchemaInspectionAreEnabled()
    {
        string path = DatabasePath();
        await using ServiceProvider provider = CreateProvider(path, CreateManifest());
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        await migrator.MigrateAsync("Primary");

        await using SqliteConnection connection = await OpenAsync(path);
        DnaXSqliteMigrationAdapter adapter = new();
        await adapter.InitializeConnectionAsync(connection, default);
        Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));
        string snapshot = await adapter.InspectSchemaAsync(connection, default);

        Assert.Contains("table|Widgets|Widgets", snapshot, StringComparison.Ordinal);
        Assert.Contains("index|UX_Widgets_Name|Widgets", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("__DnaXMigrations", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeMigrationUsesCallerSuppliedChecksum()
    {
        string path = DatabasePath();
        bool called = false;
        DnaXMigration migration = DnaXMigration.Code(
            1,
            "code-backed",
            "Code backed migration",
            DnaXMigration.ComputeChecksum("code-backed-v1"),
            async (connection, transaction, cancellationToken) =>
            {
                called = true;
                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE CodeBacked (Id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
        await using ServiceProvider provider = CreateProvider(path, new DnaXMigrationManifest(1, [migration]));

        await provider.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Primary");

        Assert.True(called);
    }

    [Fact]
    public async Task HistoricalTestScopePreservesDataDuringUpgradeAndCleansUp()
    {
        DnaXSqliteMigrationTestScope scope = await DnaXSqliteMigrationTestScope.CreateHistoricalAsync(
            CreateManifest(),
            initialVersion: 1);
        string directory = scope.DirectoryPath;
        await using (SqliteConnection connection = await scope.OpenConnectionAsync())
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Widgets (Id, Name) VALUES (7, 'preserved');";
            await insert.ExecuteNonQueryAsync();
        }

        DnaXMigrationResult result = await scope.UpgradeAsync();
        await using (SqliteConnection connection = await scope.OpenConnectionAsync())
        {
            Assert.Equal("preserved", await ScalarAsync<string>(
                connection,
                "SELECT Name FROM Widgets WHERE Id = 7;"));
        }

        Assert.Single(result.AppliedMigrations);
        await scope.DisposeAsync();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task HistoricalVerifierProducesCanonicalSchemaFromEveryVersion()
    {
        DnaXHistoricalMigrationVerification verification = await DnaXSqliteMigrationVerifier
            .VerifyAllHistoricalVersionsAsync(CreateManifest());

        Assert.Equal(2, verification.HistoricalVersions.Count);
        Assert.Equal(2, verification.HistoricalVersions[0].AppliedMigrationCount);
        Assert.Equal(1, verification.HistoricalVersions[1].AppliedMigrationCount);
        Assert.All(
            verification.HistoricalVersions,
            result => Assert.Equal(verification.CanonicalSchemaSnapshot, result.SchemaSnapshot));
    }

    [Fact]
    public async Task FailureStateIsVisibleUntilSuccessfulRetry()
    {
        string path = DatabasePath();
        int attempts = 0;
        DnaXMigration migration = DnaXMigration.Code(
            1,
            "retryable",
            "Retryable migration",
            DnaXMigration.ComputeChecksum("retryable-v1"),
            async (connection, transaction, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new TestMigrationException();
                }

                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE Retried (Id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
        await using ServiceProvider provider = CreateProvider(path, new DnaXMigrationManifest(1, [migration]));
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();

        await Assert.ThrowsAsync<DnaXMigrationFailedException>(async () => await migrator.MigrateAsync("Primary"));
        Assert.Equal(DnaXMigrationState.Failed, (await migrator.GetStatusAsync("Primary")).State);

        await migrator.MigrateAsync("Primary");
        Assert.Equal(DnaXMigrationState.Current, (await migrator.GetStatusAsync("Primary")).State);
    }

    [Fact]
    public async Task InvalidNonPositiveLedgerVersionReportsDrift()
    {
        string path = DatabasePath();
        await using ServiceProvider provider = CreateProvider(path, CreateManifest());
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        await migrator.GetStatusAsync("Primary");
        await ExecuteAsync(
            path,
            """
            INSERT INTO "__DnaXMigrations"
                ("Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc")
            VALUES
                (0, 'invalid', 'Invalid', 'sha256:0000000000000000000000000000000000000000000000000000000000000000', NULL, @now);
            """,
            ("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

        DnaXMigrationStatus status = await migrator.GetStatusAsync("Primary");

        Assert.Equal(DnaXMigrationState.Drifted, status.State);
        Assert.Contains(status.Issues, issue => issue.Contains("versions start at 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifiedExistingDatabaseCanBeBaselinedThenUpgraded()
    {
        string path = DatabasePath();
        await ExecuteAsync(path, "CREATE TABLE Widgets (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);");
        await using ServiceProvider provider = CreateProvider(path, CreateManifest());
        IDnaXDatabaseMigrator migrator = provider.GetRequiredService<IDnaXDatabaseMigrator>();
        bool verified = false;

        DnaXMigrationBaselineResult baseline = await migrator.BaselineAsync(
            "Primary",
            throughVersion: 1,
            async (context, cancellationToken) =>
            {
                verified = true;
                await using DbCommand command = context.Connection.CreateCommand();
                command.Transaction = context.Transaction;
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Widgets') WHERE name IN ('Id', 'Name');";
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);
            });
        DnaXMigrationResult upgrade = await migrator.MigrateAsync("Primary");

        Assert.True(verified);
        Assert.Equal(DnaXMigrationState.Pending, baseline.Status.State);
        Assert.Single(upgrade.AppliedMigrations);
        await using SqliteConnection connection = await OpenAsync(path);
        Assert.Equal(2L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM \"__DnaXMigrations\";"));
        await Assert.ThrowsAsync<DnaXMigrationException>(
            async () => await migrator.BaselineAsync("Primary", 2, (_, _) => ValueTask.CompletedTask));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string DatabasePath() => Path.Combine(_directory, "migration.db");

    private static DnaXMigrationManifest CreateManifest() => new(
        2,
        [
            DnaXMigration.Sql(
                1,
                "create-widgets",
                "Create widgets",
                "CREATE TABLE Widgets (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);"),
            DnaXMigration.Sql(
                2,
                "widget-name-index",
                "Index widget names",
                "CREATE UNIQUE INDEX UX_Widgets_Name ON Widgets(Name);")
        ]);

    private static ServiceProvider CreateProvider(
        string path,
        DnaXMigrationManifest manifest,
        string? applicationVersion = null,
        Action<DnaXSqliteMigrationOptions>? sqlite = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDnaXDataMigrations("Primary", options =>
        {
            options.ConnectionFactory = _ => new SqliteConnection($"Data Source={path};Pooling=False");
            options.Manifest = manifest;
            options.ApplicationVersion = applicationVersion;
            options.UseSqlite(sqlite);
        });
        return services.BuildServiceProvider();
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        string path,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = await OpenAsync(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture)!;
    }

    private sealed class TestMigrationException : Exception;
}
