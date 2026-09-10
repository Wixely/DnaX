using System.Net;

namespace DnaX.MCPFab;

/// <summary>
/// Loopback detection shared by the options validator and the host.
/// </summary>
/// <remarks>
/// Two servers already implemented this rule independently and disagreed on the test: Kodi
/// compared against a literal list (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) while ADB
/// parsed the value and called <see cref="IPAddress.IsLoopback"/>. Parsing is the correct one -
/// it recognises the whole <c>127.0.0.0/8</c> range - so that is what is shared here.
/// </remarks>
public static class McpFabHostAddress
{
    /// <summary>Determines whether a configured host binds loopback only.</summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}
