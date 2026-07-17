namespace DnaX.Caching;

public interface IDnaXCache
{
    T Hit<T>(string key, Func<T> factory, DnaXCacheEntryOptions? options = null);

    ValueTask<T> HitAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        DnaXCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default);

    bool Remove(string key);

    void Clear();
}
