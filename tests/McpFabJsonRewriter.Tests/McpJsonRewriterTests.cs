using DnaX.MCPFab.Tooling;

namespace McpFabJsonRewriter.Tests;

/// <summary>
/// Pins the emitted source, and the cases the rewriter must decline instead of guessing at.
/// </summary>
public sealed class McpJsonRewriterTests
{
    private const string Options = """
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        """;

    private static (string Source, McpJsonRewriteResult Result) Rewrite(string body, string members = "") =>
        RewriteHarness.Rewrite(RewriteHarness.Source(body, Options, members));

    [Fact]
    public void EmitsAReadableChain()
    {
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    var alias = "local";
                    return JsonSerializer.Serialize(new { alias, count = 3 }, Options);
            """);

        Assert.Equal(1, result.Rewritten);
        Assert.Contains("""McpJson.Object().Set("alias", alias).Set("count", 3).ToJsonString()""", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Serialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutesATypeWithNoOverloadThroughScalar()
    {
        (string source, _) = Rewrite("""
                    return JsonSerializer.Serialize(new { at = DateTime.UnixEpoch }, Options);
            """);

        Assert.Contains("""Set("at", McpJson.Scalar(DateTime.UnixEpoch))""", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CastsAnEnumRatherThanStringifyingIt()
    {
        (string source, _) = Rewrite("""
                    return JsonSerializer.Serialize(new { day = DayOfWeek.Friday }, Options);
            """);

        Assert.Contains("""Set("day", (long)(DayOfWeek.Friday))""", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesANamedTypePayloadAlone()
    {
        // Only anonymous types are trim-unsafe in the way this tool fixes. A named type may have a
        // generated context already, and rewriting it would be gratuitous churn.
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    return JsonSerializer.Serialize(new Uri("https://example.test"), Options);
            """);

        Assert.Equal(0, result.Rewritten);
        Assert.Contains("JsonSerializer.Serialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesAnUnrelatedSerializeMethodAlone()
    {
        // Resolved through the symbol, not the name, so a local helper called Serialize survives.
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    return Local.Serialize(new { a = 1 });
            """,
            """
                public static class Local
                {
                    public static string Serialize(object value) => value.ToString()!;
                }
            """);

        Assert.Equal(0, result.Rewritten);
        Assert.Contains("Local.Serialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinesOptionsItCannotModel()
    {
        // An unknown option changes the wire format in a way the rewrite would not reproduce, and
        // the damage would be silent - so the site is reported, not converted.
        string source = RewriteHarness.Source(
            """
                    return JsonSerializer.Serialize(new { a = 1 }, Options);
            """,
            """
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            """);

        (string rewritten, McpJsonRewriteResult result) = RewriteHarness.Rewrite(source);

        Assert.Equal(0, result.Rewritten);
        Assert.Contains("JsonSerializer.Serialize", rewritten, StringComparison.Ordinal);
        McpJsonSkip skip = Assert.Single(result.Skipped);
        Assert.Contains("SnakeCaseLower", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinesAConverterListItDoesNotModel()
    {
        string source = RewriteHarness.Source(
            """
                    return JsonSerializer.Serialize(new { a = 1 }, Options);
            """,
            """
                    Converters = { new JsonStringEnumConverter(), new CustomConverter() },
            """,
            """
                public sealed class CustomConverter : JsonConverter<int>
                {
                    public override int Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => 0;
                    public override void Write(Utf8JsonWriter w, int v, JsonSerializerOptions o) => w.WriteNumberValue(v);
                }
            """);

        (_, McpJsonRewriteResult result) = RewriteHarness.Rewrite(source);

        Assert.Equal(0, result.Rewritten);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void ReportsTheLineOfASkippedSite()
    {
        // A migration needs to find what it must do by hand, so a skip carries its location.
        string source = RewriteHarness.Source(
            """
                    return JsonSerializer.Serialize(new { a = 1 }, Options);
            """,
            """
                    PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
            """);

        (_, McpJsonRewriteResult result) = RewriteHarness.Rewrite(source);

        McpJsonSkip skip = Assert.Single(result.Skipped);
        Assert.True(skip.Line > 0);
        Assert.Contains("JsonSerializer.Serialize", skip.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinesAComplexTypeScalarWouldStringify()
    {
        // The dangerous case: Scalar's fallback is Convert.ToString, so a POCO member would compile
        // and emit "Probe+Point" instead of an object. Declining keeps the failure visible.
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    return JsonSerializer.Serialize(new { at = new Point() }, Options);
            """,
            """
                public sealed class Point { public int X { get; set; } }
            """);

        Assert.Equal(0, result.Rewritten);
        Assert.Contains("JsonSerializer.Serialize", source, StringComparison.Ordinal);
        Assert.Contains("ToString", Assert.Single(result.Skipped).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclinesASequenceOfComplexElements()
    {
        (_, McpJsonRewriteResult result) = Rewrite("""
                    return JsonSerializer.Serialize(new { points = Points }, Options);
            """,
            """
                public sealed class Point { public int X { get; set; } }
                public static readonly List<Point> Points = [];
            """);

        Assert.Equal(0, result.Rewritten);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void DoesNotInlineALocalThatIsReadTwice()
    {
        // Substituting here would evaluate the projection twice, enumerating the source again -
        // which for a live Redis cursor is a second round trip, not just wasted work.
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    var items = Names.Select(n => new { name = n });
                    var first = items.First().name;
                    return JsonSerializer.Serialize(new { first, items }, Options);
            """,
            """
                public static readonly List<string> Names = ["a"];
            """);

        Assert.Equal(0, result.Rewritten);
        Assert.Contains("JsonSerializer.Serialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotInlineALocalThatIsReassigned()
    {
        (_, McpJsonRewriteResult result) = Rewrite("""
                    var items = Names.Select(n => new { name = n });
                    items = [];
                    return JsonSerializer.Serialize(new { items }, Options);
            """,
            """
                public static readonly List<string> Names = ["a"];
            """);

        Assert.Equal(0, result.Rewritten);
    }

    [Fact]
    public void OnlyFollowsALocalWhoseTypeIsAnonymous()
    {
        // A local holding a plain list needs no inlining - the member rewrites where it stands, and
        // substituting would churn the source for nothing.
        (string source, McpJsonRewriteResult result) = Rewrite("""
                    var names = Names;
                    return JsonSerializer.Serialize(new { names }, Options);
            """,
            """
                public static readonly List<string> Names = ["a"];
            """);

        Assert.Equal(1, result.Rewritten);
        Assert.Contains("""Set("names", McpJson.Array(names,""", source, StringComparison.Ordinal);
    }

    // A tool file as the estate actually writes them: no DnaX.MCPFab import, because nothing in the
    // file needed one before the rewrite.
    private static string WithoutImportSource(string returned) =>
        $$"""
        using System.Text.Json;

        public static class Probe
        {
            public static readonly JsonSerializerOptions Options = new();

            public static string Run() => JsonSerializer.Serialize({{returned}}, Options);
        }
        """;

    [Fact]
    public void AddsTheMcpJsonImportWhenItIsMissing()
    {
        // None of RedisMCPSharp's eight tool files imported DnaX.MCPFab, so without this every
        // rewritten file failed to compile.
        (string source, McpJsonRewriteResult result) =
            RewriteHarness.Rewrite(WithoutImportSource("new { a = 1 }"));

        Assert.Equal(1, result.Rewritten);
        Assert.Equal(1, source.Split("using DnaX.MCPFab;").Length - 1);
        // Sorted position, so a later `dotnet format` pass does not move it.
        Assert.True(
            source.IndexOf("using DnaX.MCPFab;", StringComparison.Ordinal)
                < source.IndexOf("using System.Text.Json;", StringComparison.Ordinal),
            "The import should sort before System.*.");
    }

    [Fact]
    public void DoesNotAddTheImportToAFileItDidNotChange()
    {
        (string source, McpJsonRewriteResult result) =
            RewriteHarness.Rewrite(WithoutImportSource("""new System.Uri("https://example.test")"""));

        Assert.Equal(0, result.Rewritten);
        Assert.DoesNotContain("using DnaX.MCPFab;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsALeadingComment()
    {
        (string source, _) = Rewrite("""
                    // Explains why the payload looks like this.
                    return JsonSerializer.Serialize(new { a = 1 }, Options);
            """);

        Assert.Contains("// Explains why the payload looks like this.", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Count", McpJsonNaming.Verbatim, "Count")]
    [InlineData("Count", McpJsonNaming.CamelCase, "count")]
    [InlineData("count", McpJsonNaming.CamelCase, "count")]
    [InlineData("ID", McpJsonNaming.CamelCase, "id")]
    [InlineData("IOStream", McpJsonNaming.CamelCase, "ioStream")]
    [InlineData("PostId", McpJsonNaming.CamelCase, "postId")]
    public void NamingPolicyMatchesSystemTextJson(string declared, McpJsonNaming naming, string expected)
    {
        // Pinned against System.Text.Json's own policy so the table cannot drift from the runtime.
        McpJsonDialect dialect = new(naming, McpJsonEnums.Numeric);
        Assert.Equal(expected, dialect.PropertyName(declared));

        if (naming == McpJsonNaming.CamelCase)
        {
            Assert.Equal(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(declared), dialect.PropertyName(declared));
        }
    }
}
