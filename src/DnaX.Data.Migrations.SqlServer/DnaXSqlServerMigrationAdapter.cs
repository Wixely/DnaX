using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using DnaX.Data.Migrations;

namespace DnaX.Data.Migrations.SqlServer;

public sealed class DnaXSqlServerMigrationAdapter : IDnaXMigrationAdapter
{
    private readonly DnaXSqlServerMigrationOptions _options;
    private readonly string _qualifiedLedger;

    public DnaXSqlServerMigrationAdapter(DnaXSqlServerMigrationOptions? options = null)
    {
        _options = options ?? new DnaXSqlServerMigrationOptions();
        ValidateIdentifier(_options.Schema, nameof(options));
        ValidateIdentifier(_options.LedgerTable, nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.LockResource);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.LockDatabasePrincipal);
        if (_options.LockResource.Length > 255 || _options.LockTimeout < TimeSpan.Zero || _options.LockTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SQL Server lock resources are limited to 255 characters and timeout must fit a non-negative Int32 millisecond value.");
        }

        _qualifiedLedger = $"{Quote(_options.Schema)}.{Quote(_options.LedgerTable)}";
    }

    public string ProviderName => "sqlserver";

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
        try
        {
            await using DbCommand command = CreateCommand(
                connection,
                transaction,
                """
                DECLARE @result integer;
                EXECUTE @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = @lock_timeout,
                    @DbPrincipal = @database_principal;
                SELECT @result;
                """);
            command.CommandTimeout = checked((int)Math.Ceiling(_options.LockTimeout.TotalSeconds)) + 5;
            AddParameter(command, "@resource", _options.LockResource, DbType.String);
            AddParameter(command, "@lock_timeout", checked((int)_options.LockTimeout.TotalMilliseconds), DbType.Int32);
            AddParameter(command, "@database_principal", _options.LockDatabasePrincipal, DbType.String);
            int result = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw result switch
                {
                    -1 => new TimeoutException(
                        $"Could not acquire the SQL Server migration application lock within {_options.LockTimeout}."),
                    -2 => new OperationCanceledException("SQL Server canceled the migration application lock request."),
                    -3 => new InvalidOperationException("The SQL Server migration application lock was selected as a deadlock victim."),
                    _ => new InvalidOperationException($"SQL Server sp_getapplock failed with return code {result}."),
                };
            }

            return new TransactionMigrationSession(transaction);
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
            string schemaLiteral = QuoteLiteral(_options.Schema);
            string createSchema = QuoteLiteral($"CREATE SCHEMA {Quote(_options.Schema)}");
            await ExecuteAsync(
                connection,
                transaction,
                $"IF SCHEMA_ID(N'{schemaLiteral}') IS NULL EXEC(N'{createSchema}');",
                cancellationToken).ConfigureAwait(false);
        }

        string objectName = QuoteLiteral(_qualifiedLedger);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
            IF OBJECT_ID(N'{objectName}', N'U') IS NULL
            BEGIN
                CREATE TABLE {_qualifiedLedger} (
                    [Version] integer NOT NULL CONSTRAINT {Quote(ConstraintName("PK"))} PRIMARY KEY,
                    [Id] nvarchar(450) NOT NULL CONSTRAINT {Quote(ConstraintName("UQ"))} UNIQUE,
                    [Name] nvarchar(1024) NOT NULL,
                    [Checksum] char(71) NOT NULL,
                    [ApplicationVersion] nvarchar(256) NULL,
                    [AppliedAtUtc] datetimeoffset(7) NOT NULL
                );
            END;
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
            SELECT [Version], [Id], [Name], [Checksum], [ApplicationVersion], [AppliedAtUtc]
            FROM {_qualifiedLedger}
            ORDER BY [Version];
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
                ([Version], [Id], [Name], [Checksum], [ApplicationVersion], [AppliedAtUtc])
            VALUES
                (@version, @id, @name, @checksum, @application_version, @applied_at_utc);
            """);
        AddParameter(command, "@version", migration.Version, DbType.Int32);
        AddParameter(command, "@id", migration.Id, DbType.String);
        AddParameter(command, "@name", migration.Name, DbType.String);
        AddParameter(command, "@checksum", migration.Checksum, DbType.AnsiStringFixedLength);
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
                SELECT CAST('column' AS nvarchar(20)) AS object_kind,
                       s.name + '.' + t.name AS object_name,
                       RIGHT('000000' + CAST(c.column_id AS varchar(6)), 6) + ':' + c.name AS sub_name,
                       ty.name + CASE WHEN ty.name IN ('nvarchar', 'nchar') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')' ELSE '' END
                           + '|nullable=' + CAST(c.is_nullable AS varchar(1))
                           + '|default=' + COALESCE(dc.definition, '') AS definition
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns c ON c.object_id = t.object_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
                WHERE s.name = @schema AND t.name <> @ledger
                UNION ALL
                SELECT 'constraint', s.name + '.' + t.name, o.name,
                       COALESCE(cc.definition, '')
                FROM sys.objects o
                JOIN sys.tables t ON t.object_id = o.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                LEFT JOIN sys.check_constraints cc ON cc.object_id = o.object_id
                WHERE s.name = @schema AND t.name <> @ledger AND o.type IN ('C', 'F', 'PK', 'UQ')
                UNION ALL
                SELECT 'index', s.name + '.' + t.name, i.name,
                       'unique=' + CAST(i.is_unique AS varchar(1)) + '|type=' + i.type_desc
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = @schema AND t.name <> @ledger AND i.name IS NOT NULL
                UNION ALL
                SELECT 'trigger', s.name + '.' + parent.name, tr.name,
                       COALESCE(OBJECT_DEFINITION(tr.object_id), '')
                FROM sys.triggers tr
                JOIN sys.objects parent ON parent.object_id = tr.parent_id
                JOIN sys.schemas s ON s.schema_id = parent.schema_id
                WHERE s.name = @schema
                UNION ALL
                SELECT 'view', s.name + '.' + v.name, '', COALESCE(m.definition, '')
                FROM sys.views v
                JOIN sys.schemas s ON s.schema_id = v.schema_id
                LEFT JOIN sys.sql_modules m ON m.object_id = v.object_id
                WHERE s.name = @schema
            ) objects
            ORDER BY object_kind, object_name, sub_name;
            """;
        await using DbCommand command = CreateCommand(connection, transaction: null, sql);
        AddParameter(command, "@schema", _options.Schema, DbType.String);
        AddParameter(command, "@ledger", _options.LedgerTable, DbType.String);
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

    private string ConstraintName(string kind)
    {
        string name = $"{kind}_{_options.LedgerTable}";
        return name.Length <= 128 ? name : name[..128];
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
        _ => throw new InvalidOperationException("The SQL Server migration ledger contains an invalid UTC timestamp."),
    };

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string QuoteLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Escape(string value) => value.Replace("|", "||", StringComparison.Ordinal);

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("SQL Server identifiers must be at most 128 characters and cannot contain NUL.", parameterName);
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
