using System.Text.Json;
using System.Text.Json.Nodes;

namespace DnaX.MCPFab.Tests;

public sealed class McpJsonTests
{
    [Fact]
    public void SetSkipsNullsSoOutputMatchesWhenWritingNull()
    {
        string json = McpJson.Object()
            .Set("alias", "local")
            .Set("description", (string?)null)
            .Set("database", 0L)
            .Set("cluster", false)
            .ToJsonString();

        Assert.Equal("""{"alias":"local","database":0,"cluster":false}""", json);
    }

    [Fact]
    public void ArrayProjectsAndSkipsNullMappings()
    {
        JsonArray array = McpJson.Array(
            new[] { "a", "", "b" },
            value => string.IsNullOrEmpty(value) ? null : McpJson.Object().Set("v", value));

        Assert.Equal(2, array.Count);
        Assert.Equal("""[{"v":"a"},{"v":"b"}]""", array.ToJsonString());
    }

    [Fact]
    public void ScalarHandlesTheBoxedTypesADatabaseCellArrivesAs()
    {
        // The case that ruled out source-generated contexts: these arrive boxed as object, and a
        // generated resolver for object fails at runtime on anything unregistered.
        Assert.Equal("42", McpJson.Scalar(42)!.ToJsonString());
        Assert.Equal("42.5", McpJson.Scalar(42.5m)!.ToJsonString());
        Assert.Equal("true", McpJson.Scalar(true)!.ToJsonString());
        Assert.Equal("\"AQI=\"", McpJson.Scalar(new byte[] { 1, 2 })!.ToJsonString());
        Assert.Null(McpJson.Scalar(null));
        Assert.Null(McpJson.Scalar(DBNull.Value));

        string guid = McpJson.Scalar(Guid.Empty)!.ToJsonString();
        Assert.Contains("00000000-0000-0000-0000-000000000000", guid, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarFallsBackToStringRatherThanReflectionForUnknownTypes()
    {
        Assert.Equal("\"1.2.3\"", McpJson.Scalar(new Version(1, 2, 3))!.ToJsonString());
    }

    [Fact]
    public void ErrorEnvelopeHasOneShape()
    {
        string json = McpJson.Error("permission_denied", "Tool is read-only.").ToJsonString();

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("permission_denied", parsed.RootElement.GetProperty("code").GetString());
        Assert.Equal("Tool is read-only.", parsed.RootElement.GetProperty("error").GetString());
        Assert.False(parsed.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public void OversizedPayloadTruncatesIntoValidJsonRatherThanBeingCutOff()
    {
        JsonObject node = McpJson.Object().Set("blob", new string('x', 5000));
        McpFabLimitsOptions limits = new() { MaxChars = 1000 };

        string json = node.ToJson(limits);

        Assert.True(json.Length <= 1000);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1000, parsed.RootElement.GetProperty("maxChars").GetInt32());
    }

    [Fact]
    public void PayloadWithinLimitIsReturnedVerbatim()
    {
        JsonObject node = McpJson.Object().Set("alias", "local");

        Assert.Equal("""{"alias":"local"}""", node.ToJson(new McpFabLimitsOptions()));
        Assert.Equal("""{"alias":"local"}""", node.ToJson(null));
    }

    [Fact]
    public void BuiltNodesRoundTripThroughTheTrimSafePath()
    {
        // Guards the whole premise: JsonNode has built-in converters, so this must not need a
        // JsonSerializerContext or reflection to render.
        JsonObject node = McpJson.Object()
            .Set("nested", McpJson.Object().Set("n", 1L))
            .Set("items", McpJson.Array(new[] { 1, 2 }, i => McpJson.Scalar(i)));

        Assert.Equal("""{"nested":{"n":1},"items":[1,2]}""", node.ToJsonString());
    }
}
