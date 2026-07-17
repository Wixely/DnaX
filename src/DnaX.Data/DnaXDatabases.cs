using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnaX.Data;

internal sealed class DnaXDatabases : IDnaXDatabases
{
    private readonly IReadOnlyDictionary<string, DnaXDatabaseRegistration> _registrations;
    private readonly IServiceProvider _services;
    private readonly ILogger<DnaXDatabases> _logger;

    public DnaXDatabases(
        IEnumerable<DnaXDatabaseRegistration> registrations,
        IServiceProvider services,
        ILogger<DnaXDatabases>? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger<DnaXDatabases>.Instance;

        Dictionary<string, DnaXDatabaseRegistration> byName = new(StringComparer.Ordinal);
        foreach (DnaXDatabaseRegistration registration in registrations)
        {
            if (!byName.TryAdd(registration.Name, registration))
            {
                throw new InvalidOperationException($"Database '{registration.Name}' is registered more than once.");
            }
        }

        _registrations = byName;
    }

    public T Execute<T>(string name, Func<DbConnection, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using DbConnection connection = CreateConnection(name);
        EnsureOpen(connection);
        return operation(connection);
    }

    public void Execute(string name, Action<DbConnection> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using DbConnection connection = CreateConnection(name);
        EnsureOpen(connection);
        operation(connection);
    }

    public async ValueTask<T> ExecuteAsync<T>(
        string name,
        Func<DbConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using DbConnection connection = CreateConnection(name);
        await EnsureOpenAsync(connection, cancellationToken).ConfigureAwait(false);
        return await operation(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ExecuteAsync(
        string name,
        Func<DbConnection, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using DbConnection connection = CreateConnection(name);
        await EnsureOpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await operation(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<T> ExecuteInTransactionAsync<T>(
        string name,
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> operation,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using DbConnection connection = CreateConnection(name);
        await EnsureOpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            T result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception operationException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                _logger.LogError(
                    rollbackException,
                    "Rollback failed for database {DatabaseName} after operation failure {OperationExceptionType}.",
                    name,
                    operationException.GetType().FullName);
            }

            throw;
        }
    }

    private DbConnection CreateConnection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_registrations.TryGetValue(name, out DnaXDatabaseRegistration? registration))
        {
            throw new DnaXDatabaseNotFoundException(name, _registrations.Keys);
        }

        return registration.ConnectionFactory(_services)
            ?? throw new InvalidOperationException($"The connection factory for '{name}' returned null.");
    }

    private static void EnsureOpen(DbConnection connection)
    {
        if (connection.State == ConnectionState.Closed)
        {
            connection.Open();
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"The connection is in state '{connection.State}' after opening.");
        }
    }

    private static async ValueTask EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"The connection is in state '{connection.State}' after opening.");
        }
    }
}
