using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Oracle;
using DnaX.Data.Migrations.PostgreSql;
using DnaX.Data.Migrations.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Migrations.Tests;

public sealed class ProviderAdapterContractTests
{
    [Fact]
    public async Task PostgreSqlAdapterUsesTransactionAdvisoryLockAndQuotedLedger()
    {
        ScriptedDbConnection connection = new();
        connection.ScalarResults.Enqueue(true);
        DnaXPostgreSqlMigrationAdapter adapter = new(new()
        {
            Schema = "app-data",
            LedgerTable = "migration-ledger",
        });

        await adapter.InitializeConnectionAsync(connection, default);
        await using IDnaXMigrationSession session = await adapter.AcquireSessionAsync(connection, default);
        await ExerciseAdapterAsync(adapter, connection, session);
        await session.CommitAsync(default);

        Assert.Equal(DnaXMigrationAtomicity.AtomicChain, adapter.Atomicity);
        Assert.NotNull(session.Transaction);
        Assert.True(connection.LastTransaction?.Committed);
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("pg_try_advisory_xact_lock", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("\"app-data\".\"migration-ledger\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SqlServerAdapterUsesTransactionOwnedApplicationLockAndQuotedLedger()
    {
        ScriptedDbConnection connection = new();
        connection.ScalarResults.Enqueue(0);
        DnaXSqlServerMigrationAdapter adapter = new(new()
        {
            Schema = "app data",
            LedgerTable = "migration ledger",
        });

        await adapter.InitializeConnectionAsync(connection, default);
        await using IDnaXMigrationSession session = await adapter.AcquireSessionAsync(connection, default);
        await ExerciseAdapterAsync(adapter, connection, session);
        await session.CommitAsync(default);

        Assert.Equal(DnaXMigrationAtomicity.AtomicChain, adapter.Atomicity);
        Assert.NotNull(session.Transaction);
        Assert.True(connection.LastTransaction?.Committed);
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("sys.sp_getapplock", StringComparison.Ordinal) &&
            command.CommandText.Contains("@LockOwner = 'Transaction'", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("[app data].[migration ledger]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OracleAdapterUsesSessionLockAndDurableCheckpointsWithoutTransaction()
    {
        ScriptedDbConnection connection = new();
        DnaXOracleMigrationAdapter adapter = new(new()
        {
            Schema = "APP DATA",
            LedgerTable = "MIGRATION LEDGER",
        });

        await adapter.InitializeConnectionAsync(connection, default);
        await using (IDnaXMigrationSession session = await adapter.AcquireSessionAsync(connection, default))
        {
            await ExerciseAdapterAsync(adapter, connection, session);
            await session.CheckpointAsync(default);
            await session.CommitAsync(default);

            Assert.Null(session.Transaction);
        }

        Assert.Equal(DnaXMigrationAtomicity.ProviderManagedCheckpoints, adapter.Atomicity);
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("DBMS_LOCK.REQUEST", StringComparison.Ordinal) &&
            command.CommandText.Contains("release_on_commit => FALSE", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("\"APP DATA\".\"MIGRATION LEDGER\"", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            string.Equals(command.CommandText, "COMMIT", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            command.CommandText.Contains("DBMS_LOCK.RELEASE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_073_741_824)]
    public void OracleRejectsInvalidDbmsLockIdentifiers(int lockId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DnaXOracleMigrationAdapter(new() { LockId = lockId }));
    }

    [Fact]
    public async Task ProviderManagedRunnerCheckpointsBeforeALaterMigrationFails()
    {
        ScriptedDbConnection connection = new();
        DnaXMigrationManifest manifest = new(
            2,
            [
                DnaXMigration.Code(
                    1,
                    "first",
                    "First",
                    DnaXMigration.ComputeChecksum("first-v1"),
                    async (database, transaction, cancellationToken) =>
                    {
                        Assert.Null(transaction);
                        await using DbCommand command = database.CreateCommand();
                        command.CommandText = "CREATE TABLE FIRST_TABLE (ID NUMBER)";
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }),
                DnaXMigration.Code(
                    2,
                    "second",
                    "Second",
                    DnaXMigration.ComputeChecksum("second-v1"),
                    (_, transaction, _) =>
                    {
                        Assert.Null(transaction);
                        return ValueTask.FromException(new TestProviderMigrationException());
                    })
            ]);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDnaXDataMigrations("Oracle", options =>
        {
            options.ConnectionFactory = _ => connection;
            options.Manifest = manifest;
            options.Adapter = new DnaXOracleMigrationAdapter();
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        DnaXMigrationFailedException failure = await Assert.ThrowsAsync<DnaXMigrationFailedException>(
            async () => await provider.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync("Oracle"));

        Assert.Equal(2, failure.Migration.Version);
        Assert.Single(
            connection.Commands,
            command => command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            string.Equals(command.CommandText, "COMMIT", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, command =>
            string.Equals(command.CommandText, "ROLLBACK", StringComparison.Ordinal));
    }

    private static async Task ExerciseAdapterAsync(
        IDnaXMigrationAdapter adapter,
        ScriptedDbConnection connection,
        IDnaXMigrationSession session)
    {
        await adapter.EnsureLedgerAsync(connection, session.Transaction, default);
        Assert.Empty(await adapter.ReadLedgerAsync(connection, session.Transaction, default));
        await adapter.RecordAppliedAsync(
            connection,
            session.Transaction,
            new(
                1,
                "create-items",
                "Create items",
                DnaXMigration.ComputeChecksum("create-items-v1"),
                "test",
                DateTimeOffset.UnixEpoch),
            default);
        Assert.Equal(string.Empty, await adapter.InspectSchemaAsync(connection, default));
    }

    private sealed class ScriptedDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public Queue<object?> ScalarResults { get; } = new();

        public List<ScriptedDbCommand> Commands { get; } = [];

        public ScriptedDbTransaction? LastTransaction { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "Test";

        public override string DataSource => "Test";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            LastTransaction = new(this, isolationLevel);

        protected override DbCommand CreateDbCommand()
        {
            ScriptedDbCommand command = new(this);
            Commands.Add(command);
            return command;
        }

        internal object? NextScalar() => ScalarResults.Count == 0 ? 0 : ScalarResults.Dequeue();

        internal static DbDataReader CreateReader(string commandText)
        {
            DataTable table = new();
            if (commandText.Contains("ORDER BY \"Version\"", StringComparison.Ordinal) ||
                commandText.Contains("ORDER BY [Version]", StringComparison.Ordinal) ||
                commandText.Contains("ORDER BY \"VERSION_NUMBER\"", StringComparison.Ordinal))
            {
                table.Columns.Add("Version", typeof(int));
                table.Columns.Add("Id", typeof(string));
                table.Columns.Add("Name", typeof(string));
                table.Columns.Add("Checksum", typeof(string));
                table.Columns.Add("ApplicationVersion", typeof(string));
                table.Columns.Add("AppliedAtUtc", typeof(string));
            }
            else
            {
                table.Columns.Add("ObjectKind", typeof(string));
                table.Columns.Add("ObjectName", typeof(string));
                table.Columns.Add("SubName", typeof(string));
                table.Columns.Add("Definition", typeof(string));
            }

            return table.CreateDataReader();
        }
    }

    private sealed class ScriptedDbTransaction(
        ScriptedDbConnection connection,
        IsolationLevel isolationLevel) : DbTransaction
    {
        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public override IsolationLevel IsolationLevel => isolationLevel;

        protected override DbConnection DbConnection => connection;

        public override void Commit() => Committed = true;

        public override void Rollback() => RolledBack = true;
    }

    private sealed class ScriptedDbCommand(ScriptedDbConnection connection) : DbCommand
    {
        private readonly ScriptedDbParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        [AllowNull]
        protected override DbConnection DbConnection { get; set; } = connection;

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery()
        {
            foreach (DbParameter parameter in _parameters)
            {
                if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue)
                {
                    parameter.Value = 0;
                }
            }

            return 0;
        }

        public override object? ExecuteScalar() => connection.NextScalar();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new ScriptedDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ScriptedDbConnection.CreateReader(CommandText);
    }

    private sealed class ScriptedDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() => DbType = DbType.Object;
    }

    private sealed class ScriptedDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (object value in values)
            {
                Add(value);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _items.FindIndex(item => string.Equals(item.ParameterName, parameterName, StringComparison.Ordinal));

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            int index = IndexOf(parameterName);
            if (index < 0)
            {
                _items.Add(value);
            }
            else
            {
                _items[index] = value;
            }
        }
    }

    private sealed class TestProviderMigrationException : Exception;
}
