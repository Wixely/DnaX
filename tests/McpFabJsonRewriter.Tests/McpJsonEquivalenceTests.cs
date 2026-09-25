using DnaX.MCPFab.Tooling;

namespace McpFabJsonRewriter.Tests;

/// <summary>
/// Before-and-after proof: each case runs the original reflection-based call and the rewritten
/// <c>McpJson</c> chain, and requires identical JSON.
/// </summary>
/// <remarks>
/// These are the tests that matter. A rewrite that compiles and returns a different shape is
/// exactly the failure mode this whole effort exists to remove, and no amount of source comparison
/// would catch it.
/// </remarks>
public sealed class McpJsonEquivalenceTests
{
    private const string Options = """
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    ReferenceHandler = ReferenceHandler.IgnoreCycles,
        """;

    private static string Run(string body, string members = "") =>
        RewriteHarness.Equivalent(RewriteHarness.Source(body, Options, members));

    [Fact]
    public void ExplicitMembersKeepTheirNamesAndValues()
    {
        string json = Run("""
                    var alias = "redis.boyleuat.com";
                    var key = "session:1";
                    return JsonSerializer.Serialize(new { alias, key, count = 42, truncated = false }, Options);
            """);

        Assert.Equal("""{"alias":"redis.boyleuat.com","key":"session:1","count":42,"truncated":false}""", json);
    }

    [Fact]
    public void NullMembersStayOmitted()
    {
        // WhenWritingNull dropped these; McpJson.Set drops them too. The risk was a rewrite that
        // started emitting "note":null and changed every payload that has an optional field.
        string json = Run("""
                    string? note = null;
                    var alias = "local";
                    return JsonSerializer.Serialize(new { alias, note }, Options);
            """);

        Assert.Equal("""{"alias":"local"}""", json);
        Assert.DoesNotContain("note", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedAnonymousObjectsAreRewrittenInPlace()
    {
        string json = Run("""
                    var host = "db-01";
                    return JsonSerializer.Serialize(
                        new { alias = "prod", server = new { host, port = 6379 } }, Options);
            """);

        Assert.Equal("""{"alias":"prod","server":{"host":"db-01","port":6379}}""", json);
    }

    [Fact]
    public void SelectProjectionsBecomeMcpJsonArray()
    {
        // The shape at CollectionTools.cs:217 - a Take/Select of an anonymous element type.
        string json = Run("""
                    return JsonSerializer.Serialize(
                        new
                        {
                            count = Entries.Count,
                            items = Entries.Take(2).Select(e => new { value = e.Key, score = e.Value }),
                        },
                        Options);
            """,
            """
                public static readonly List<KeyValuePair<string, double>> Entries =
                [
                    new("alpha", 1.5),
                    new("beta", 2.5),
                    new("gamma", 3.5),
                ];
            """);

        Assert.Equal("""{"count":3,"items":[{"value":"alpha","score":1.5},{"value":"beta","score":2.5}]}""", json);
    }

    [Theory]
    // Every one of these binds to no Set overload as written, so a rewriter that passed the
    // expression straight through would not compile. They must route via McpJson.Scalar.
    [InlineData("new DateTime(2026, 9, 25, 8, 30, 0, DateTimeKind.Utc)")]
    [InlineData("Guid.Parse(\"2f1c9f7e-0b7a-4c1d-9f2e-8a6b5c4d3e2f\")")]
    [InlineData("(ulong)18446744073709551615")]
    [InlineData("new byte[] { 1, 2, 3 }")]
    [InlineData("TimeSpan.FromMinutes(90)")]
    public void TypesWithNoSetOverloadRoundTripThroughScalar(string expression)
    {
        Run($$"""
                    return JsonSerializer.Serialize(new { value = {{expression}} }, Options);
            """);
    }

    [Theory]
    [InlineData("(short)7")]
    [InlineData("(byte)7")]
    [InlineData("(uint)7")]
    [InlineData("7L")]
    [InlineData("7.5f")]
    [InlineData("7.5d")]
    [InlineData("7.5m")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    [InlineData("DateTimeOffset.UnixEpoch")]
    public void TypesWithASetOverloadArePassedStraightThrough(string expression)
    {
        Run($$"""
                    return JsonSerializer.Serialize(new { value = {{expression}} }, Options);
            """);
    }

    [Fact]
    public void EnumsStayNumericWhenNoStringConverterIsRegistered()
    {
        // System.Text.Json writes an enum as its number by default. McpJson.Scalar would have
        // written the member name, silently changing the wire format for every enum in the estate.
        string json = Run("""
                    return JsonSerializer.Serialize(new { state = DayOfWeek.Wednesday }, Options);
            """);

        Assert.Equal("""{"state":3}""", json);
    }

    [Fact]
    public void EnumsBecomeNamesWhenAStringConverterIsRegistered()
    {
        // MailCalMCPSharp is the one server that registers JsonStringEnumConverter.
        string source = RewriteHarness.Source(
            """
                    return JsonSerializer.Serialize(new { state = DayOfWeek.Wednesday }, Options);
            """,
            """
                    Converters = { new JsonStringEnumConverter() },
            """);

        string json = RewriteHarness.Equivalent(source);
        Assert.Equal("""{"state":"Wednesday"}""", json);
    }

    [Fact]
    public void CamelCasePolicyIsAppliedToDeclaredNames()
    {
        // WordpressMCPSharp is the one server that sets a naming policy, and it has PascalCase
        // members. A verbatim rewrite would rename every field it emits.
        string source = RewriteHarness.Source(
            """
                    return JsonSerializer.Serialize(new { Count = 2, PostId = 17, ID = 4 }, Options);
            """,
            """
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            """);

        string json = RewriteHarness.Equivalent(source);
        Assert.Equal("""{"count":2,"postId":17,"id":4}""", json);
    }

    [Fact]
    public void AScalarProjectionBecomesAnArrayOfValues()
    {
        // The one site the first run against RedisMCPSharp declined: a Select to a scalar rather
        // than to an anonymous object.
        string json = Run("""
                    return JsonSerializer.Serialize(
                        new { fields = Names.Select(f => (string?)f) }, Options);
            """,
            """
                public static readonly List<string> Names = ["alpha", "beta"];
            """);

        Assert.Equal("""{"fields":["alpha","beta"]}""", json);
    }

    [Fact]
    public void ACollectionMemberBecomesAnArrayRatherThanItsToString()
    {
        // Without this, the member routes to McpJson.Scalar, which compiles and writes
        // "System.Collections.Generic.List`1[System.String]" into the payload.
        string json = Run("""
                    return JsonSerializer.Serialize(new { keys = Names, count = Names.Count }, Options);
            """,
            """
                public static readonly List<string> Names = ["a", "b", "c"];
            """);

        Assert.Equal("""{"keys":["a","b","c"],"count":3}""", json);
        Assert.DoesNotContain("System.Collections", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrayOfNumbersStaysAnArrayOfNumbers()
    {
        string json = Run("""
                    return JsonSerializer.Serialize(new { ports = new[] { 6379, 6380 } }, Options);
            """);

        Assert.Equal("""{"ports":[6379,6380]}""", json);
    }

    [Fact]
    public void ByteArraysStayBase64RatherThanBecomingNumberArrays()
    {
        // System.Text.Json writes byte[] as base64. Treating it as a sequence would silently turn
        // every binary value in the estate into an array of integers.
        string json = Run("""
                    return JsonSerializer.Serialize(new { blob = new byte[] { 1, 2, 3 } }, Options);
            """);

        Assert.Equal("""{"blob":"AQID"}""", json);
    }

    [Fact]
    public void AnArrayOfEnumsKeepsItsNumericForm()
    {
        string json = Run("""
                    return JsonSerializer.Serialize(
                        new { days = new[] { DayOfWeek.Monday, DayOfWeek.Friday } }, Options);
            """);

        Assert.Equal("""{"days":[1,5]}""", json);
    }

    [Fact]
    public void ADictionaryStaysAnObjectRatherThanBecomingAnArrayOfPairs()
    {
        // A dictionary is also an IEnumerable<KeyValuePair<,>>, so the sequence rule would have
        // turned every map in the estate into [{"Key":..,"Value":..}].
        string json = Run("""
                    return JsonSerializer.Serialize(new { fields = Fields }, Options);
            """,
            """
                public static readonly Dictionary<string, string> Fields =
                    new() { ["host"] = "db-01", ["role"] = "primary" };
            """);

        Assert.Equal("""{"fields":{"host":"db-01","role":"primary"}}""", json);
    }

    [Fact]
    public void ANullDictionaryValueIsWrittenRatherThanOmitted()
    {
        // WhenWritingNull applies to properties, not to dictionary values - an entry that exists
        // with a null value is not the same as an absent key.
        string json = Run("""
                    return JsonSerializer.Serialize(new { fields = Fields }, Options);
            """,
            """
                public static readonly Dictionary<string, string?> Fields =
                    new() { ["present"] = "yes", ["missing"] = null };
            """);

        Assert.Equal("""{"fields":{"present":"yes","missing":null}}""", json);
    }

    [Fact]
    public void ADictionaryOfBoxedValuesKeepsEachRuntimeType()
    {
        // The SQLMCPSharp shape: cells boxed as object, typed only at runtime.
        string json = Run("""
                    return JsonSerializer.Serialize(new { row = Row }, Options);
            """,
            """
                public static readonly Dictionary<string, object?> Row = new()
                {
                    ["id"] = 7,
                    ["name"] = "alpha",
                    ["ratio"] = 1.5d,
                    ["active"] = true,
                    ["absent"] = null,
                };
            """);

        Assert.Equal("""{"row":{"id":7,"name":"alpha","ratio":1.5,"active":true,"absent":null}}""", json);
    }

    [Fact]
    public void AProjectionHeldInALocalIsFollowed()
    {
        // The shape at CollectionTools.cs:217 as it is actually written: the projection is in a
        // local, so the anonymous type cannot be reached from the member that uses it.
        string json = Run("""
                    var trimmed = Entries.Take(2).Select(e => new { value = e.Key, score = e.Value });
                    return JsonSerializer.Serialize(new { count = Entries.Count, items = trimmed }, Options);
            """,
            """
                public static readonly List<KeyValuePair<string, double>> Entries =
                    [new("alpha", 1.5), new("beta", 2.5), new("gamma", 3.5)];
            """);

        Assert.Equal("""{"count":3,"items":[{"value":"alpha","score":1.5},{"value":"beta","score":2.5}]}""", json);
    }

    [Fact]
    public void AListOfDictionariesNestsCorrectly()
    {
        string json = Run("""
                    return JsonSerializer.Serialize(new { rows = Rows }, Options);
            """,
            """
                public static readonly List<Dictionary<string, string>> Rows =
                [
                    new() { ["id"] = "1", ["name"] = "alpha" },
                    new() { ["id"] = "2", ["name"] = "beta" },
                ];
            """);

        Assert.Equal("""{"rows":[{"id":"1","name":"alpha"},{"id":"2","name":"beta"}]}""", json);
    }

    [Fact]
    public void ADictionaryOfListsNestsCorrectly()
    {
        string json = Run("""
                    return JsonSerializer.Serialize(new { groups = Groups }, Options);
            """,
            """
                public static readonly Dictionary<string, List<string>> Groups =
                    new() { ["a"] = ["one", "two"], ["b"] = [] };
            """);

        Assert.Equal("""{"groups":{"a":["one","two"],"b":[]}}""", json);
    }

    [Fact]
    public void AProjectionWithAStatementBodyIsRewrittenInPlace()
    {
        // The shape in DiagnosticTools: the lambda does work before building its payload.
        string json = Run("""
                    return JsonSerializer.Serialize(
                        new
                        {
                            entries = Lines.Select(line =>
                            {
                                var parts = line.Split(':');
                                return new { name = parts[0], value = long.Parse(parts[1]) };
                            }),
                        },
                        Options);
            """,
            """
                public static readonly List<string> Lines = ["alpha:1", "beta:2"];
            """);

        Assert.Equal("""{"entries":[{"name":"alpha","value":1},{"name":"beta","value":2}]}""", json);
    }

    [Fact]
    public void AJsonNodeMemberIsNotDoubleEncoded()
    {
        string json = Run("""
                    JsonNode node = JsonNode.Parse("{\"nested\":true}")!;
                    return JsonSerializer.Serialize(new { payload = node }, Options);
            """);

        Assert.Equal("""{"payload":{"nested":true}}""", json);
    }
}
