using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.Oracle;

public sealed class DnaXOracleMigrationAdapter : IDnaXMigrationAdapter
{
    private readonly DnaXOracleMigrationOptions _options;
    private readonly string _qualifiedLedger;

    public DnaXOracleMigrationAdapter(DnaXOracleMigrationOptions? options = null)
    {
        _options = options ?? new DnaXOracleMigrationOptions();
        if (_options.Schema is not null)
        {
            ValidateIdentifier(_options.Schema, nameof(options));
        }

        ValidateIdentifier(_options.LedgerTable, nameof(options));
        if (_options.LockId is < 0 or > 1_073_741_823)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Oracle DBMS_LOCK identifiers range from 0 to 1073741823.");
        }

        if (_options.LockTimeout < TimeSpan.Zero || _options.LockRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Oracle lock timeout cannot be negative and retry delay must be positive.");
        }

        _qualifiedLedger = _options.Schema is null
            ? Quote(_options.LedgerTable)
            : $"{Quote(_options.Schema)}.{Quote(_options.LedgerTable)}";
    }

    public string ProviderName => "oracle";

    public DnaXMigrationAtomicity Atomicity => DnaXMigrationAtomicity.ProviderManagedCheckpoints;

    public ValueTask InitializeConnectionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public async ValueTask<IDnaXMigrationSession> AcquireSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int result = await ExecuteDbmsLockAsync(
                connection,
                """
                BEGIN
                    :result := DBMS_LOCK.REQUEST(
                        id => :lock_id,
                        lockmode => DBMS_LOCK.X_MODE,
                        timeout => 0,
                        release_on_commit => FALSE);
                END;
                """,
                _options.LockId,
                cancellationToken).ConfigureAwait(false);
            if (result is 0 or 4)
            {
                return new OracleMigrationSession(connection, _options.LockId);
            }

            if (result != 1)
            {
                throw new InvalidOperationException($"Oracle DBMS_LOCK.REQUEST failed with return code {result}.");
            }

            if (Stopwatch.GetElapsedTime(started) >= _options.LockTimeout)
            {
                throw new TimeoutException(
                    $"Could not acquire the Oracle migration DBMS_LOCK within {_options.LockTimeout}.");
            }

            await Task.Delay(_options.LockRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask EnsureLedgerAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string primaryKey = Quote(TrimIdentifier($"PK_{_options.LedgerTable}"));
        string uniqueKey = Quote(TrimIdentifier($"UQ_{_options.LedgerTable}_ID"));
        string ddl = $"""
            CREATE TABLE {_qualifiedLedger} (
                "VERSION_NUMBER" NUMBER(10) NOT NULL,
                "MIGRATION_ID" VARCHAR2(255 CHAR) NOT NULL,
                "MIGRATION_NAME" VARCHAR2(1000 CHAR) NOT NULL,
                "CHECKSUM" CHAR(71 CHAR) NOT NULL,
                "APPLICATION_VERSION" VARCHAR2(256 CHAR) NULL,
                "APPLIED_AT_UTC" TIMESTAMP(7) WITH TIME ZONE NOT NULL,
                CONSTRAINT {primaryKey} PRIMARY KEY ("VERSION_NUMBER"),
                CONSTRAINT {uniqueKey} UNIQUE ("MIGRATION_ID")
            )
            """;
        string block = $"""
            BEGIN
                EXECUTE IMMEDIATE '{QuoteLiteral(ddl)}';
            EXCEPTION
                WHEN OTHERS THEN
                    IF SQLCODE != -955 THEN
                        RAISE;
                    END IF;
            END;
            """;
        return ExecuteAsync(connection, transaction: null, block, cancellationToken);
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
            SELECT "VERSION_NUMBER", "MIGRATION_ID", "MIGRATION_NAME", "CHECKSUM", "APPLICATION_VERSION",
                   TO_CHAR("APPLIED_AT_UTC", 'YYYY-MM-DD"T"HH24:MI:SS.FF7TZH:TZM')
            FROM {_qualifiedLedger}
            ORDER BY "VERSION_NUMBER"
            """);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<DnaXAppliedMigration> applied = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string timestamp = reader.GetString(5);
            if (!DateTimeOffset.TryParseExact(
                    timestamp,
                    "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset appliedAt))
            {
                throw new InvalidOperationException("The Oracle migration ledger contains an invalid UTC timestamp.");
            }

            applied.Add(new(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                appliedAt.ToUniversalTime()));
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
                ("VERSION_NUMBER", "MIGRATION_ID", "MIGRATION_NAME", "CHECKSUM", "APPLICATION_VERSION", "APPLIED_AT_UTC")
            VALUES
                (:version_number, :migration_id, :migration_name, :checksum, :application_version,
                 TO_TIMESTAMP_TZ(:applied_at_utc, 'YYYY-MM-DD"T"HH24:MI:SS.FF7TZH:TZM'))
            """);
        AddParameter(command, "version_number", migration.Version, DbType.Int32);
        AddParameter(command, "migration_id", migration.Id, DbType.String);
        AddParameter(command, "migration_name", migration.Name, DbType.String);
        AddParameter(command, "checksum", migration.Checksum, DbType.AnsiStringFixedLength);
        AddParameter(command, "application_version", migration.ApplicationVersion, DbType.String);
        AddParameter(
            command,
            "applied_at_utc",
            migration.AppliedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            DbType.String);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> InspectSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        string owner = _options.Schema is null
            ? "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')"
            : $"'{QuoteLiteral(_options.Schema)}'";
        string ledger = $"'{QuoteLiteral(_options.LedgerTable)}'";
        string sql = $"""
            SELECT object_kind, object_name, sub_name, definition
            FROM (
                SELECT 'COLUMN' AS object_kind,
                       TABLE_NAME AS object_name,
                       LPAD(TO_CHAR(COLUMN_ID), 6, '0') || ':' || COLUMN_NAME AS sub_name,
                       DATA_TYPE || CASE WHEN DATA_TYPE LIKE '%CHAR%' THEN '(' || TO_CHAR(CHAR_LENGTH) || ')' ELSE '' END
                           || '|nullable=' || NULLABLE AS definition
                FROM ALL_TAB_COLUMNS
                WHERE OWNER = {owner} AND TABLE_NAME <> {ledger}
                UNION ALL
                SELECT 'CONSTRAINT', TABLE_NAME, CONSTRAINT_NAME,
                       CONSTRAINT_TYPE || '|status=' || STATUS || '|deferred=' || DEFERRED
                FROM ALL_CONSTRAINTS
                WHERE OWNER = {owner} AND TABLE_NAME <> {ledger}
                UNION ALL
                SELECT 'INDEX', TABLE_NAME, INDEX_NAME,
                       UNIQUENESS || '|type=' || INDEX_TYPE || '|status=' || STATUS
                FROM ALL_INDEXES
                WHERE OWNER = {owner} AND TABLE_NAME <> {ledger}
                UNION ALL
                SELECT 'INDEX_COLUMN', TABLE_NAME, INDEX_NAME,
                       LPAD(TO_CHAR(COLUMN_POSITION), 6, '0') || ':' || COLUMN_NAME
                FROM ALL_IND_COLUMNS
                WHERE INDEX_OWNER = {owner} AND TABLE_NAME <> {ledger}
                UNION ALL
                SELECT 'TRIGGER', TABLE_NAME, TRIGGER_NAME,
                       STATUS || '|' || TRIGGERING_EVENT || '|' || TRIGGER_TYPE
                FROM ALL_TRIGGERS
                WHERE OWNER = {owner} AND TABLE_NAME <> {ledger}
                UNION ALL
                SELECT OBJECT_TYPE, OBJECT_NAME, '', STATUS
                FROM ALL_OBJECTS
                WHERE OWNER = {owner}
                  AND OBJECT_TYPE IN ('VIEW', 'MATERIALIZED VIEW', 'SEQUENCE', 'PROCEDURE', 'FUNCTION', 'PACKAGE')
            ) objects
            ORDER BY object_kind, object_name, sub_name
            """;
        await using DbCommand command = CreateCommand(connection, transaction: null, sql);
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

    private static async ValueTask<int> ExecuteDbmsLockAsync(
        DbConnection connection,
        string sql,
        int lockId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CreateCommand(connection, transaction: null, sql);
        DbParameter result = command.CreateParameter();
        result.ParameterName = "result";
        result.DbType = DbType.Int32;
        result.Direction = ParameterDirection.Output;
        command.Parameters.Add(result);
        AddParameter(command, "lock_id", lockId, DbType.Int32);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result.Value, CultureInfo.InvariantCulture);
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

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal);

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TrimIdentifier(string value) => value.Length <= 128 ? value : value[..128];

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Oracle identifiers must be at most 128 characters and cannot contain NUL.", parameterName);
        }
    }

    private sealed class OracleMigrationSession(DbConnection connection, int lockId) : IDnaXMigrationSession
    {
        private bool _released;

        public DbTransaction? Transaction => null;

        public ValueTask CheckpointAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(connection, transaction: null, "COMMIT", cancellationToken);

        public ValueTask CommitAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(connection, transaction: null, "COMMIT", cancellationToken);

        public ValueTask RollbackAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(connection, transaction: null, "ROLLBACK", cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            int result = await ExecuteDbmsLockAsync(
                connection,
                "BEGIN :result := DBMS_LOCK.RELEASE(id => :lock_id); END;",
                lockId,
                CancellationToken.None).ConfigureAwait(false);
            if (result is not 0 and not 4)
            {
                throw new InvalidOperationException($"Oracle DBMS_LOCK.RELEASE failed with return code {result}.");
            }
        }
    }
}
