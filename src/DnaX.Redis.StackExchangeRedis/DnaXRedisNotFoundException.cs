namespace DnaX.Redis;

public sealed class DnaXRedisNotFoundException : InvalidOperationException
{
    public DnaXRedisNotFoundException(string name, IEnumerable<string> registeredNames)
        : base($"Redis resource '{name}' is not registered. Registered resources: {string.Join(", ", registeredNames)}.")
    {
        Name = name;
    }

    public string Name { get; }
}
