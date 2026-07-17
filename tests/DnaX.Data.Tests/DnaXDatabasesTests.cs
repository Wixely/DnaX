using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DnaX.Data;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Tests;

public sealed class DnaXDatabasesTests
{
    [Fact]
    public void OpensPassesAndDisposesNamedConnection()
    {
        FakeDbConnection? created = null;
        using ServiceProvider provider = CreateProvider(builder =>
            builder.AddDatabase("Primary", () => created = new FakeDbConnection()));
        IDnaXDatabases databases = provider.GetRequiredService<IDnaXDatabases>();

        int result = databases.Execute("Primary", connection =>
        {
            Assert.Same(created, connection);
            Assert.Equal(ConnectionState.Open, connection.State);
            return 42;
        });

        Assert.Equal(42, result);
        Assert.NotNull(created);
        Assert.True(created.WasDisposed);
    }

    [Fact]
    public void DisposesConnectionWhenClosureThrows()
    {
        FakeDbConnection? created = null;
        using ServiceProvider provider = CreateProvider(builder =>
            builder.AddDatabase("Primary", () => created = new FakeDbConnection()));
        IDnaXDatabases databases = provider.GetRequiredService<IDnaXDatabases>();

        Assert.Throws<TestException>(() =>
            databases.Execute<int>("Primary", _ => throw new TestException()));

        Assert.NotNull(created);
        Assert.True(created.WasDisposed);
    }

    [Fact]
    public async Task RollsBackTransactionAndPreservesOperationException()
    {
        FakeDbConnection? created = null;
        await using ServiceProvider provider = CreateProvider(builder =>
            builder.AddDatabase("Oracle", () => created = new FakeDbConnection()));
        IDnaXDatabases databases = provider.GetRequiredService<IDnaXDatabases>();

        await Assert.ThrowsAsync<TestException>(async () =>
            await databases.ExecuteInTransactionAsync<int>(
                "Oracle",
                (_, _, _) => ValueTask.FromException<int>(new TestException())));

        Assert.NotNull(created?.LastTransaction);
        Assert.True(created.LastTransaction.RolledBack);
        Assert.False(created.LastTransaction.Committed);
        Assert.True(created.WasDisposed);
    }

    [Fact]
    public void UnknownNameListsConfiguredResources()
    {
        using ServiceProvider provider = CreateProvider(builder =>
            builder.AddDatabase("Known", () => new FakeDbConnection()));
        IDnaXDatabases databases = provider.GetRequiredService<IDnaXDatabases>();

        DnaXDatabaseNotFoundException exception = Assert.Throws<DnaXDatabaseNotFoundException>(
            () => databases.Execute("Missing", _ => 0));

        Assert.Contains("Known", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(Action<DnaXDataBuilder> configure)
    {
        ServiceCollection services = new();
        services.AddLogging();
        DnaXDataBuilder builder = services.AddDnaXData();
        configure(builder);
        return services.BuildServiceProvider();
    }

    private sealed class TestException : Exception;

    private sealed class FakeDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public bool WasDisposed { get; private set; }
        public FakeDbTransaction? LastTransaction { get; private set; }
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "Fake";
        public override string DataSource => "Fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            LastTransaction = new FakeDbTransaction(this, isolationLevel);

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            _state = ConnectionState.Closed;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeDbTransaction(FakeDbConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public bool Committed { get; private set; }
        public bool RolledBack { get; private set; }
        public override IsolationLevel IsolationLevel => isolationLevel;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Committed = true;
        public override void Rollback() => RolledBack = true;
    }
}
