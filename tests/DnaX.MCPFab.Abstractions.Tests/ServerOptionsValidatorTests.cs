using Microsoft.Extensions.Options;

namespace DnaX.MCPFab.Tests;

public sealed class ServerOptionsValidatorTests
{
    private static readonly ServerOptionsValidator Validator = new();

    private static ServerOptions Valid() => new()
    {
        Host = "localhost",
        Port = 5713,
        Path = "/mcp",
        WindowsServiceName = "RedisMCPSharp",
    };

    [Fact]
    public void LoopbackWithoutPasswordIsAllowed()
    {
        // MCPHub launches managed servers with no credentials and registers them with no headers,
        // so requiring a secret on loopback would make every managed server unreachable.
        foreach (string host in new[] { "localhost", "LOCALHOST", "127.0.0.1", "127.0.0.5", "::1" })
        {
            ServerOptions options = Valid();
            options.Host = host;

            Assert.True(Validator.Validate(null, options).Succeeded, host);
        }
    }

    [Fact]
    public void NonLoopbackWithoutPasswordFails()
    {
        ServerOptions options = Valid();
        options.Host = "0.0.0.0";

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("binds beyond loopback", StringComparison.Ordinal));
    }

    [Fact]
    public void NonLoopbackWithShortPasswordFails()
    {
        ServerOptions options = Valid();
        options.Host = "10.0.0.7";
        options.Password = new string('x', ServerOptionsValidator.MinimumPasswordLength - 1);

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("at least", StringComparison.Ordinal));
    }

    [Fact]
    public void NonLoopbackWithAdequatePasswordSucceeds()
    {
        ServerOptions options = Valid();
        options.Host = "10.0.0.7";
        options.Password = new string('x', ServerOptionsValidator.MinimumPasswordLength);

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortFails(int port)
    {
        ServerOptions options = Valid();
        options.Port = port;

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mcp")]
    [InlineData("/mcp?x=1")]
    [InlineData("/mcp#frag")]
    public void InvalidPathFails(string path)
    {
        ServerOptions options = Valid();
        options.Path = path;

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Fact]
    public void FailuresAreReportedTogetherRatherThanOneAtATime()
    {
        ServerOptions options = new() { Host = "0.0.0.0", Port = 0, Path = "nope" };

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 3);
    }
}
