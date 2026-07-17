using System.Data;
using System.Data.Common;

namespace DnaX.Data;

public interface IDnaXDatabases
{
    T Execute<T>(string name, Func<DbConnection, T> operation);

    void Execute(string name, Action<DbConnection> operation);

    ValueTask<T> ExecuteAsync<T>(
        string name,
        Func<DbConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);

    ValueTask ExecuteAsync(
        string name,
        Func<DbConnection, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);

    ValueTask<T> ExecuteInTransactionAsync<T>(
        string name,
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> operation,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        CancellationToken cancellationToken = default);
}
