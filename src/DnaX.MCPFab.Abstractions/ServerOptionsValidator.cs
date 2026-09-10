using Microsoft.Extensions.Options;

namespace DnaX.MCPFab;

/// <summary>
/// Startup validation for <see cref="ServerOptions"/>.
/// </summary>
/// <remarks>
/// Only three of twenty servers validated anything before MCPFab; the other seventeen surfaced an
/// invalid port or path as an unhandled Kestrel exception reported as "Server terminated
/// unexpectedly". Registering this with <c>ValidateOnStart</c> gives every server the same
/// diagnosis at the same moment.
/// </remarks>
public sealed class ServerOptionsValidator : IValidateOptions<ServerOptions>
{
    /// <summary>
    /// Shortest accepted secret. Matches the existing check in ADBMCPSharp; a short shared secret
    /// on a publicly bound port is barely better than none.
    /// </summary>
    public const int MinimumPasswordLength = 24;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"Server:Port must be between 1 and 65535 but was {options.Port}.");
        }

        if (string.IsNullOrWhiteSpace(options.Path)
            || !options.Path.StartsWith('/')
            || options.Path.Contains('?', StringComparison.Ordinal)
            || options.Path.Contains('#', StringComparison.Ordinal))
        {
            failures.Add("Server:Path must be an absolute path without a query string or fragment, for example /mcp.");
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add("Server:Host must be set, for example localhost or 0.0.0.0.");
        }
        else if (!options.IsLoopback)
        {
            // The rule that matters. Binding beyond loopback exposes every tool this server
            // carries, so a secret stops being optional at exactly that point.
            if (string.IsNullOrWhiteSpace(options.Password))
            {
                failures.Add(
                    $"Server:Password is required when Server:Host ('{options.Host}') binds beyond loopback. "
                    + "Set a password, or bind to localhost.");
            }
            else if (options.Password.Length < MinimumPasswordLength)
            {
                failures.Add(
                    $"Server:Password must be at least {MinimumPasswordLength} characters when binding beyond loopback.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
