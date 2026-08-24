using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DnaX.Data.Migrations;
using Microsoft.Data.Sqlite;

namespace DnaX.Data.Migrations.Sqlite;

public sealed class DnaXSqliteMigrationAdapter : IDnaXMigrationAdapter
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private readonly DnaXSqliteMigrationOptions _options;

    public DnaXSqliteMigrationAdapter(DnaXSqliteMigrationOptions? options = null)
    {
        _options = options ?? new DnaXSqliteMigrationOptions();
        if (_options.LockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The SQLite migration lock timeout cannot be negative.");
        }

        if (_options.LockRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The SQLite migration lock retry delay must be positive.");
        }
    }

    public string ProviderName => "sqlite";

    public async ValueTask InitializeConnectionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        SqliteConnection sqlite = RequireSqlite(connection);
        // Microsoft.Data.Sqlite's internal BEGIN command only accepts whole-second
        // timeouts. Keep it short, then perform the configured, cancellable retry here.
        sqlite.DefaultTimeout = 1;
        StringBuilder pragmas = new();
        pragmas.AppendLine(_options.EnforceForeignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");
        pragmas.AppendLine("PRAGMA busy_timeout = 0;");
        await ExecuteAsync(sqlite, transaction: null, pragmas.ToString(), cancellationToken).ConfigureAwait(false);

        if (_options.EnableWriteAheadLogging)
        {
            await ExecuteScalarAsync(sqlite, transaction: null, "PRAGMA journal_mode = WAL;", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<DbTransaction> AcquireLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        SqliteConnection sqlite = RequireSqlite(connection);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SqliteTransaction transaction = sqlite.BeginTransaction(deferred: false);
                try
                {
                    if (_options.EnforceForeignKeys && _options.DeferForeignKeysDuringMigration)
                    {
                        await ExecuteAsync(
                            sqlite,
                            transaction,
                            "PRAGMA defer_foreign_keys = ON;",
                            cancellationToken).ConfigureAwait(false);
                    }

                    return transaction;
                }
                catch
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode is SqliteBusy or SqliteLocked)
            {
                if (Stopwatch.GetElapsedTime(started) >= _options.LockTimeout)
                {
                    throw new TimeoutException(
                        $"Could not acquire the SQLite migration lock within {_options.LockTimeout}.",
                        exception);
                }

                await Task.Delay(_options.LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask EnsureLedgerAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            RequireSqlite(connection),
            transaction,
            """
            CREATE TABLE IF NOT EXISTS "__DnaXMigrations" (
                "Version" INTEGER NOT NULL PRIMARY KEY,
                "Id" TEXT NOT NULL UNIQUE,
                "Name" TEXT NOT NULL,
                "Checksum" TEXT NOT NULL,
                "ApplicationVersion" TEXT NULL,
                "AppliedAtUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);

    public async ValueTask<IReadOnlyList<DnaXAppliedMigration>> ReadLedgerAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        SqliteConnection sqlite = RequireSqlite(connection);
        await using DbCommand command = CreateCommand(
            sqlite,
            transaction,
            """
            SELECT "Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc"
            FROM "__DnaXMigrations"
            ORDER BY "Version";
            """);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<DnaXAppliedMigration> applied = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string appliedAtText = reader.GetString(5);
            if (!DateTimeOffset.TryParseExact(
                    appliedAtText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset appliedAt))
            {
                throw new InvalidOperationException(
                    $"Migration ledger version {reader.GetInt32(0)} has an invalid UTC timestamp.");
            }

            applied.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                appliedAt));
        }

        return applied.AsReadOnly();
    }

    public async ValueTask RecordAppliedAsync(
        DbConnection connection,
        DbTransaction transaction,
        DnaXAppliedMigration migration,
        CancellationToken cancellationToken)
    {
        SqliteConnection sqlite = RequireSqlite(connection);
        await using DbCommand command = CreateCommand(
            sqlite,
            transaction,
            """
            INSERT INTO "__DnaXMigrations"
                ("Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc")
            VALUES
                (@version, @id, @name, @checksum, @applicationVersion, @appliedAtUtc);
            """);
        AddParameter(command, "@version", migration.Version);
        AddParameter(command, "@id", migration.Id);
        AddParameter(command, "@name", migration.Name);
        AddParameter(command, "@checksum", migration.Checksum);
        AddParameter(command, "@applicationVersion", migration.ApplicationVersion);
        AddParameter(command, "@appliedAtUtc", migration.AppliedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> InspectSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        SqliteConnection sqlite = RequireSqlite(connection);
        await using DbCommand command = CreateCommand(
            sqlite,
            transaction: null,
            """
            SELECT type, name, tbl_name, COALESCE(sql, '')
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
              AND name <> '__DnaXMigrations'
            ORDER BY type, name;
            """);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        StringBuilder snapshot = new();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot.Append(reader.GetString(0))
                .Append('|')
                .Append(reader.GetString(1))
                .Append('|')
                .Append(reader.GetString(2))
                .AppendLine();
            snapshot.AppendLine(NormalizeSql(reader.GetString(3)));
        }

        return snapshot.ToString();
    }

    private static SqliteConnection RequireSqlite(DbConnection connection) =>
        connection as SqliteConnection
        ?? throw new InvalidOperationException(
            $"The SQLite migration adapter requires {typeof(SqliteConnection).FullName}, " +
            $"but the connection factory returned {connection.GetType().FullName}.");

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(
        SqliteConnection connection,
        DbTransaction? transaction,
        string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        return string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
