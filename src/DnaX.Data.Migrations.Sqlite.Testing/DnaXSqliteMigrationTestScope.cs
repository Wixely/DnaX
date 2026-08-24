using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Migrations.Sqlite.Testing;

public sealed class DnaXSqliteMigrationTestOptions
{
    public string DatabaseName { get; set; } = "Test";

    public string? ApplicationVersion { get; set; } = "test";

    public Action<DnaXSqliteMigrationOptions>? ConfigureSqlite { get; set; }

    public Func<DnaXBeforeMigrationContext, CancellationToken, ValueTask>? BeforeMigrateAsync { get; set; }
}

public sealed class DnaXSqliteMigrationTestScope : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly DnaXSqliteMigrationAdapter _inspector = new();
    private bool _disposed;

    private DnaXSqliteMigrationTestScope(
        string directoryPath,
        string databasePath,
        string databaseName,
        ServiceProvider services)
    {
        DirectoryPath = directoryPath;
        DatabasePath = databasePath;
        DatabaseName = databaseName;
        _services = services;
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public string DatabaseName { get; }

    public string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    public IDnaXDatabaseMigrator Migrator => _services.GetRequiredService<IDnaXDatabaseMigrator>();

    public static async ValueTask<DnaXSqliteMigrationTestScope> CreateAsync(
        DnaXMigrationManifest manifest,
        Action<DnaXSqliteMigrationTestOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        DnaXSqliteMigrationTestScope scope = await CreateHistoricalAsync(
            manifest,
            initialVersion: 0,
            configure,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await scope.UpgradeAsync(cancellationToken).ConfigureAwait(false);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<DnaXSqliteMigrationTestScope> CreateHistoricalAsync(
        DnaXMigrationManifest manifest,
        int initialVersion,
        Action<DnaXSqliteMigrationTestOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (initialVersion < 0 || initialVersion > manifest.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(initialVersion));
        }

        DnaXSqliteMigrationTestOptions options = new();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "dnax-sqlite-migrations",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "database.db");

        try
        {
            if (initialVersion > 0)
            {
                DnaXMigrationManifest historicalManifest = new(
                    initialVersion,
                    manifest.Migrations.Take(initialVersion));
                await using ServiceProvider historicalServices = CreateServices(
                    databasePath,
                    options,
                    historicalManifest);
                await historicalServices.GetRequiredService<IDnaXDatabaseMigrator>()
                    .MigrateAsync(options.DatabaseName, cancellationToken)
                    .ConfigureAwait(false);
            }

            ServiceProvider services = CreateServices(databasePath, options, manifest);
            return new(directory, databasePath, options.DatabaseName, services);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }

    public ValueTask<DnaXMigrationResult> UpgradeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Migrator.MigrateAsync(DatabaseName, cancellationToken);
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SqliteConnection connection = new(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await _inspector.InitializeConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<string> InspectSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _inspector.InspectSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _services.DisposeAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static ServiceProvider CreateServices(
        string databasePath,
        DnaXSqliteMigrationTestOptions options,
        DnaXMigrationManifest manifest)
    {
        ServiceCollection services = new();
        services.AddDnaXDataMigrations(options.DatabaseName, migrationOptions =>
        {
            migrationOptions.ConnectionFactory = _ =>
                new SqliteConnection($"Data Source={databasePath};Pooling=False");
            migrationOptions.Manifest = manifest;
            migrationOptions.ApplicationVersion = options.ApplicationVersion;
            migrationOptions.BeforeMigrateAsync = options.BeforeMigrateAsync;
            migrationOptions.UseSqlite(options.ConfigureSqlite);
        });
        return services.BuildServiceProvider();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
