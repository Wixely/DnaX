namespace DnaX.MCPFab.Tests;

public sealed class McpFabTargetRegistryTests
{
    private sealed class TestEntry : McpFabTargetEntry
    {
        public string ConnectionString { get; set; } = string.Empty;
    }

    private static TestEntry Entry(string alias = "", bool enabled = true) =>
        new() { Alias = alias, Enabled = enabled };

    [Fact]
    public void GeneratesAliasesInConfigurationOrder()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry(), Entry(), Entry()], aliasPrefix: "redis");

        Assert.Equal(["redis-1", "redis-2", "redis-3"], registry.Entries.Select(e => e.Alias));
        Assert.Empty(registry.Problems);
    }

    [Fact]
    public void RequirePolicyReportsMissingAliasInsteadOfInventingOne()
    {
        McpFabTargetRegistry<TestEntry> registry = McpFabTargetRegistry<TestEntry>.Create(
            [Entry("kitchen"), Entry()],
            aliasPrefix: "kodi",
            policy: McpFabAliasPolicy.Require);

        Assert.Single(registry.Problems);
        Assert.Contains("must declare an Alias", registry.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsToFirstEnabledEntryWhenNoDefaultAliasIsConfigured()
    {
        McpFabTargetRegistry<TestEntry> registry = McpFabTargetRegistry<TestEntry>.Create(
            [Entry("a", enabled: false), Entry("b"), Entry("c")]);

        Assert.Equal("b", registry.Default!.Alias);
    }

    [Fact]
    public void DisabledEntriesAreKeptButNeverResolved()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("live"), Entry("retired", enabled: false)]);

        Assert.Equal(2, registry.Entries.Count);
        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryResolve("retired", out _));
    }

    [Fact]
    public void UnknownDefaultAliasIsReportedRatherThanSilentlyIgnored()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("a")], defaultAlias: "missing");

        Assert.Contains(
            registry.Problems,
            problem => problem.Contains("does not name an enabled", StringComparison.Ordinal));
        // Still resolvable so the operator can start the server and fix the config.
        Assert.Equal("a", registry.Default!.Alias);
    }

    [Fact]
    public void DuplicateAliasesAreReported()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("dup"), Entry("DUP")]);

        Assert.Contains(
            registry.Problems,
            problem => problem.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedAliasIsReported()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("has space")]);

        Assert.Contains(
            registry.Problems,
            problem => problem.Contains("whitespace", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveIsCaseInsensitiveAndFallsBackToDefault()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("Prod"), Entry("dev")], defaultAlias: "dev");

        Assert.Equal("Prod", registry.Resolve("prod").Alias);
        Assert.Equal("dev", registry.Resolve(null).Alias);
        Assert.Equal("dev", registry.Resolve("  ").Alias);
    }

    [Fact]
    public void UnknownAliasThrowsNamingWhatIsConfigured()
    {
        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.Create([Entry("alpha"), Entry("beta")]);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => registry.Resolve("gamma"));

        // The message has to let an agent correct itself rather than retry the same alias.
        Assert.Contains("alpha", error.Message, StringComparison.Ordinal);
        Assert.Contains("beta", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapShapedRegistryUsesTheKeyAsTheAlias()
    {
        Dictionary<string, TestEntry> map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["site1"] = new TestEntry { ConnectionString = "one" },
            ["site2"] = new TestEntry { ConnectionString = "two" },
        };

        McpFabTargetRegistry<TestEntry> registry =
            McpFabTargetRegistry<TestEntry>.CreateFromMap(map, defaultAlias: "site2");

        Assert.Empty(registry.Problems);
        Assert.Equal("two", registry.Resolve("site2").ConnectionString);
        Assert.Equal("site2", registry.Default!.Alias);
    }
}

/// <summary>Regression cover for aliases that real deployments actually use.</summary>
public sealed class McpFabTargetRegistryAliasFormatTests
{
    private sealed class Entry : McpFabTargetEntry;

    [Theory]
    [InlineData("redis.boyleuat.com")]      // the alias v1.2.0 rejected
    [InlineData("redis.boylesports.com")]
    [InlineData("db-01:6379")]
    [InlineData("plain")]
    [InlineData("with_underscore")]
    [InlineData("with-hyphen")]
    public void HostnameAndEndpointAliasesAreAccepted(string alias)
    {
        McpFabTargetRegistry<Entry> registry =
            McpFabTargetRegistry<Entry>.Create([new Entry { Alias = alias }]);

        Assert.Empty(registry.Problems);
        Assert.Equal(alias, registry.Resolve(alias).Alias);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("has\nnewline")]
    public void WhitespaceAliasesAreStillRejected(string alias)
    {
        McpFabTargetRegistry<Entry> registry =
            McpFabTargetRegistry<Entry>.Create([new Entry { Alias = alias }]);

        Assert.NotEmpty(registry.Problems);
    }
}
