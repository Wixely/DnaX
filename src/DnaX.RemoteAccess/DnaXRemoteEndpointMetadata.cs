namespace DnaX.RemoteAccess;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class DnaXRemoteActionAttribute(string action) : Attribute
{
    public string Action { get; } = string.IsNullOrWhiteSpace(action)
        ? throw new ArgumentException("The remote action cannot be empty.", nameof(action))
        : action;
}
