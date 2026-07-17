using StackExchange.Redis;

namespace DnaX.Redis;

public interface IDnaXRedis
{
    T Execute<T>(string name, Func<IDatabase, T> operation);

    void Execute(string name, Action<IDatabase> operation);

    ValueTask<T> ExecuteAsync<T>(
        string name,
        Func<IDatabase, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);

    ValueTask ExecuteAsync(
        string name,
        Func<IDatabase, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default);
}
