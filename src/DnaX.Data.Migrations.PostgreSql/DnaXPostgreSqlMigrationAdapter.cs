using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.PostgreSql;

public sealed class DnaXPostgreSqlMigrationAdapter : IDnaXMigrationAdapter
{
    private readonly DnaXPostgreSqlMigrationOptions _options;
    private readonly string _qualifiedLedger;

    public DnaXPostgreSqlMigrationAdapter(DnaXPostgreSqlMigrationOptions? options = null)
    {
        _options = options ?? new DnaXPostgreSqlMigrationOptions();
        ValidateIdentifier(_options.Schema, nameof(options), 63);
        ValidateIdentifier(_options.LedgerTable, nameof(options), 63);
        ValidateTiming(_options.LockTimeout, _options.LockRetryDelay, nameof(options));
        _qualifiedLedger = $"{Quote(_options.Schema)}.{Quote(_options.LedgerTable)}";
    }

    public string ProviderName => "postgresql";

    public DnaXMigrationAtomicity Atomicity => DnaXMigrationAtomicity.AtomicChain;

    public ValueTask InitializeConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public async ValueTask<IDnaXMigrationSession> AcquireSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        DbTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using DbCommand command = CreateCommand(
                    connection,
                    transaction,
                    "SELECT pg_try_advisory_xact_lock(@lock_key);");
                AddParameter(command, "@lock_key", _options.AdvisoryLockKey, DbType.Int64);
                bool acquired = Convert.ToBoolean(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (acquired)
                {
                    return new TransactionMigrationSession(transaction);
                }

                if (Stopwatch.GetElapsedTime(started) >= _options.LockTimeout)
                {
                    throw new TimeoutException(
                        $"Could not acquire the PostgreSQL migration advisory lock within {_options.LockTimeout}.");
                }

                await Task.Delay(_options.LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask EnsureLedgerAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (_options.CreateSchemaIfMissing)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE SCHEMA IF NOT EXISTS {Quote(_options.Schema)};",
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"""
            CREATE TABLE IF NOT EXISTS {_qualifiedLedger} (
                "Version" integer NOT NULL PRIMARY KEY,
                "Id" text NOT NULL UNIQUE,
                "Name" text NOT NULL,
                "Checksum" text NOT NULL,
                "ApplicationVersion" text NULL,
                "AppliedAtUtc" timestamp with time zone NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DnaXAppliedMigration>> ReadLedgerAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(
            connection,
            transaction,
            $"""
            SELECT "Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc"
            FROM {_qualifiedLedger}
            ORDER BY "Version";
            """);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<DnaXAppliedMigration> applied = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ReadTimestamp(reader.GetValue(5))));
        }

        return applied.AsReadOnly();
    }

    public async ValueTask RecordAppliedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        DnaXAppliedMigration migration,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(
            connection,
            transaction,
            $"""
            INSERT INTO {_qualifiedLedger}
                ("Version", "Id", "Name", "Checksum", "ApplicationVersion", "AppliedAtUtc")
            VALUES
                (@version, @id, @name, @checksum, @application_version, @applied_at_utc);
            """);
        AddParameter(command, "@version", migration.Version, DbType.Int32);
        AddParameter(command, "@id", migration.Id, DbType.String);
        AddParameter(command, "@name", migration.Name, DbType.String);
        AddParameter(command, "@checksum", migration.Checksum, DbType.String);
        AddParameter(command, "@application_version", migration.ApplicationVersion, DbType.String);
        AddParameter(command, "@applied_at_utc", migration.AppliedAtUtc, DbType.DateTimeOffset);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> InspectSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT object_kind, object_name, sub_name, definition
            FROM (
                SELECT 'column'::text AS object_kind,
                       table_schema || '.' || table_name AS object_name,
                       LPAD(ordinal_position::text, 6, '0') || ':' || column_name AS sub_name,
                       data_type || '|nullable=' || is_nullable || '|default=' || COALESCE(column_default, '') AS definition
                FROM information_schema.columns
                WHERE table_schema = @schema AND table_name <> @ledger
                UNION ALL
                SELECT 'constraint', n.nspname || '.' || c.relname, con.conname,
                       pg_get_constraintdef(con.oid, true)
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname <> @ledger
                UNION ALL
                SELECT 'index', schemaname || '.' || tablename, indexname, indexdef
                FROM pg_indexes
                WHERE schemaname = @schema AND tablename <> @ledger
                UNION ALL
                SELECT 'trigger', n.nspname || '.' || c.relname, t.tgname,
                       pg_get_triggerdef(t.oid, true)
                FROM pg_trigger t
                JOIN pg_class c ON c.oid = t.tgrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname <> @ledger AND NOT t.tgisinternal
                UNION ALL
                SELECT 'view', schemaname || '.' || viewname, '', definition
                FROM pg_views
                WHERE schemaname = @schema
            ) objects
            ORDER BY object_kind, object_name, sub_name;
            """;
        await using DbCommand command = CreateCommand(connection, transaction: null, sql);
        AddParameter(command, "@schema", _options.Schema, DbType.String);
        AddParameter(command, "@ledger", _options.LedgerTable, DbType.String);
        return await ReadSnapshotAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value, DbType type)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadTimestamp(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed) => parsed.ToUniversalTime(),
        _ => throw new InvalidOperationException("The PostgreSQL migration ledger contains an invalid UTC timestamp."),
    };

    private static async ValueTask<string> ReadSnapshotAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        StringBuilder snapshot = new();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot.Append(Escape(reader.GetString(0))).Append('|')
                .Append(Escape(reader.GetString(1))).Append('|')
                .Append(Escape(reader.GetString(2))).AppendLine();
            snapshot.AppendLine(NormalizeSql(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return snapshot.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal);

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void ValidateIdentifier(string value, string parameterName, int maximumUtf8Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                $"PostgreSQL identifiers must be at most {maximumUtf8Bytes} UTF-8 bytes and cannot contain NUL.",
                parameterName);
        }
    }

    private static void ValidateTiming(TimeSpan timeout, TimeSpan retryDelay, string parameterName)
    {
        if (timeout < TimeSpan.Zero || retryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Lock timeout cannot be negative and retry delay must be positive.");
        }
    }

    private sealed class TransactionMigrationSession(DbTransaction transaction) : IDnaXMigrationSession
    {
        public DbTransaction Transaction => transaction;

        public ValueTask CheckpointAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CommitAsync(CancellationToken cancellationToken) => new(transaction.CommitAsync(cancellationToken));

        public ValueTask RollbackAsync(CancellationToken cancellationToken) => new(transaction.RollbackAsync(cancellationToken));

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
