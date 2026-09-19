using System.Text.Json;
using ModelContextProtocol;

namespace DnaX.MCPFab.Tests;

/// <summary>
/// The default SDK reply to a throwing tool is "An error occurred invoking '&lt;tool&gt;'", with the
/// cause only in the server log - so an agent cannot correct itself. These pin both halves of the
/// replacement: guidance reaches the caller, unexpected detail does not.
/// </summary>
public sealed class McpFabToolErrorsTests
{
    private static JsonElement Describe(Exception exception) =>
        JsonDocument.Parse(McpFabToolErrors.DescribeFailure("sample_tool", exception)).RootElement.Clone();

    [Theory]
    [InlineData("No Redis servers configured.")]
    [InlineData("Operation 'flushall' is destructive and disabled. Set Redis:AllowDestructive=true to permit it.")]
    [InlineData("Unknown alias 'gamma'. Configured aliases: alpha, beta.")]
    public void DeliberateRefusalsReachTheCaller(string message)
    {
        JsonElement payload = Describe(new InvalidOperationException(message));

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("tool_refused", payload.GetProperty("code").GetString());
        Assert.Equal(message, payload.GetProperty("error").GetString());
        Assert.Equal("sample_tool", payload.GetProperty("tool").GetString());
    }

    [Fact]
    public void EveryDeliberateExceptionTypeIsSurfaced()
    {
        // These are the types a tool throws on purpose; their text is written for the caller.
        Exception[] deliberate =
        [
            new McpException("set returnImage=true or save=true."),
            new InvalidOperationException("read-only mode."),
            new ArgumentException("key must not be empty."),
            new NotSupportedException("HEXPIRE needs Redis 7.4."),
            new KeyNotFoundException("no such field."),
        ];

        foreach (Exception exception in deliberate)
        {
            JsonElement payload = Describe(exception);
            Assert.Equal("tool_refused", payload.GetProperty("code").GetString());
            Assert.Equal(exception.Message, payload.GetProperty("error").GetString());
        }
    }

    [Fact]
    public void UnexpectedExceptionMessagesAreNotLeaked()
    {
        // An unexpected message can carry a connection string, a path, or key material.
        const string Secret = "redis-prod:6379,password=hunter2";
        JsonElement payload = Describe(new InvalidDataException(Secret));

        string error = payload.GetProperty("error").GetString()!;
        Assert.Equal("tool_failed", payload.GetProperty("code").GetString());
        Assert.DoesNotContain("hunter2", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error, StringComparison.Ordinal);
        // The type is still named, so the caller knows what kind of failure it was.
        Assert.Contains(nameof(InvalidDataException), error, StringComparison.Ordinal);
        Assert.Contains("sample_tool", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadIsAlwaysValidJsonWithTheSharedShape()
    {
        foreach (Exception exception in new Exception[]
        {
            new InvalidOperationException("refused"),
            new InvalidDataException("unexpected"),
        })
        {
            JsonElement payload = Describe(exception);

            Assert.False(payload.GetProperty("ok").GetBoolean());
            Assert.True(payload.TryGetProperty("code", out _));
            Assert.True(payload.TryGetProperty("error", out _));
            Assert.Equal("sample_tool", payload.GetProperty("tool").GetString());
        }
    }
}
