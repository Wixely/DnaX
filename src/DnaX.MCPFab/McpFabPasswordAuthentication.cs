using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace DnaX.MCPFab;

/// <summary>Authentication scheme names used by MCPFab.</summary>
public static class McpFabAuthentication
{
    /// <summary>Scheme name for the shared-secret handler.</summary>
    public const string SchemeName = "McpFabPassword";

    /// <summary>Header accepted alongside Bearer and Basic, kept from the existing servers.</summary>
    public const string HeaderName = "X-MCP-Password";
}

/// <summary>
/// Shared-secret authentication accepting <c>X-MCP-Password</c>, <c>Authorization: Bearer</c>, and
/// HTTP Basic (any user name).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the hand-rolled middleware that was byte-identical modulo namespace in seventeen
/// servers, with two corrections. The original compared with <c>StringComparison.Ordinal</c>,
/// which is not constant time; this uses <see cref="CryptographicOperations.FixedTimeEquals"/>,
/// as Kodi and ADB already did.
/// </para>
/// <para>
/// A blank password authenticates every request. That is deliberate and is constrained at the
/// edge instead: <see cref="ServerOptionsValidator"/> refuses to start a server that binds beyond
/// loopback without one. It must stay permitted on loopback because MCPHub launches managed
/// servers with no credentials and registers upstreams with no headers, so requiring a secret
/// unconditionally would make every managed server unreachable.
/// </para>
/// </remarks>
public sealed class McpFabPasswordAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<ServerOptions> _server;

    /// <summary>Creates the handler.</summary>
    public McpFabPasswordAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ServerOptions> server)
        : base(options, logger, encoder)
    {
        _server = server;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Read per request rather than caching: the product config is registered with
        // reloadOnChange, so rotating the secret must take effect without a restart.
        string expected = _server.CurrentValue.Password;

        if (string.IsNullOrWhiteSpace(expected))
        {
            return Task.FromResult(Success("anonymous"));
        }

        return Task.FromResult(
            TryReadSuppliedSecret(out string? supplied) && Matches(supplied, expected)
                ? Success("mcp")
                : AuthenticateResult.Fail("MCP password required."));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer, Basic";
        return Response.WriteAsync("MCP password required.");
    }

    private static AuthenticateResult Success(string name)
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Name, name)],
            McpFabAuthentication.SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), McpFabAuthentication.SchemeName));
    }

    private bool TryReadSuppliedSecret(out string? secret)
    {
        if (Request.Headers.TryGetValue(McpFabAuthentication.HeaderName, out Microsoft.Extensions.Primitives.StringValues header)
            && !string.IsNullOrEmpty(header))
        {
            secret = header.ToString();
            return true;
        }

        string? authorization = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(authorization)
            || !AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? parsed)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            secret = null;
            return false;
        }

        if (string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            secret = parsed.Parameter;
            return true;
        }

        if (string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            // The user name is ignored: these servers have one secret, not user accounts.
            // Kept because existing clients are configured this way.
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
                int separator = decoded.IndexOf(':', StringComparison.Ordinal);
                if (separator >= 0)
                {
                    secret = decoded[(separator + 1)..];
                    return true;
                }
            }
            catch (FormatException)
            {
                // Malformed Basic credentials are a failed attempt, not a server error.
            }
        }

        secret = null;
        return false;
    }

    private static bool Matches(string? supplied, string expected)
    {
        if (supplied is null)
        {
            return false;
        }

        // Compare hashes rather than raw bytes so a length difference does not leak through the
        // comparison, and so FixedTimeEquals always sees equal-length inputs.
        Span<byte> suppliedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied), suppliedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}
