namespace DnaX.Caching;

public sealed class DnaXCacheTypeMismatchException : InvalidOperationException
{
    public DnaXCacheTypeMismatchException(string key, Type storedType, Type requestedType)
        : base($"Cache key '{key}' contains {storedType.FullName}, but {requestedType.FullName} was requested. Remove the key before changing its value type.")
    {
        Key = key;
        StoredType = storedType;
        RequestedType = requestedType;
    }

    public string Key { get; }

    public Type StoredType { get; }

    public Type RequestedType { get; }
}
