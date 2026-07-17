namespace DnaX.Data;

public sealed class DnaXDatabaseNotFoundException : InvalidOperationException
{
    public DnaXDatabaseNotFoundException(string name, IEnumerable<string> registeredNames)
        : base($"Database '{name}' is not registered. Registered databases: {string.Join(", ", registeredNames)}.")
    {
        Name = name;
    }

    public string Name { get; }
}
