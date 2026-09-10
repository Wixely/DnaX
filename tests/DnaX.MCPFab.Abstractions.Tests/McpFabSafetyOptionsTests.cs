namespace DnaX.MCPFab.Tests;

public sealed class McpFabSafetyOptionsTests
{
    [Fact]
    public void ReadsAreAlwaysAllowedAndDefaultPostureIsReadOnly()
    {
        McpFabSafetyOptions safety = new();

        Assert.True(safety.ReadOnly);
        Assert.False(safety.AllowDestructive);
        Assert.True(safety.IsAllowed("redis_get", mutates: false));
        Assert.False(safety.IsAllowed("redis_set", mutates: true));
    }

    [Fact]
    public void ReadOnlyFalseDoesNotOnItsOwnUnlockTheDestructiveTail()
    {
        // The layering these servers deliberately chose: one switch must not unlock deletion.
        McpFabSafetyOptions safety = new() { ReadOnly = false };

        Assert.True(safety.IsAllowed("redis_set", mutates: true));
        Assert.False(safety.IsAllowed("redis_del", mutates: true, destructive: true));

        safety.AllowDestructive = true;
        Assert.True(safety.IsAllowed("redis_del", mutates: true, destructive: true));
    }

    [Fact]
    public void PerToolGateWinsInBothDirections()
    {
        McpFabSafetyOptions safety = new() { ReadOnly = false, AllowDestructive = true };
        safety.Gates["redis_flushall"] = false;
        Assert.False(safety.IsAllowed("redis_flushall", mutates: true, destructive: true));

        McpFabSafetyOptions locked = new();
        locked.Gates["redis_set"] = true;
        Assert.True(locked.IsAllowed("redis_set", mutates: true));
    }

    [Fact]
    public void GatesAreCaseInsensitive()
    {
        McpFabSafetyOptions safety = new();
        safety.Gates["Redis_Set"] = true;

        Assert.True(safety.IsAllowed("redis_set", mutates: true));
    }

    [Fact]
    public void RefusalNamesTheKeyThatWouldPermitIt()
    {
        McpFabSafetyOptions safety = new();

        Assert.Contains("Redis:ReadOnly", safety.DescribeRefusal("Redis", "redis_set"), StringComparison.Ordinal);

        safety.ReadOnly = false;
        Assert.Contains(
            "Redis:AllowDestructive",
            safety.DescribeRefusal("Redis", "redis_del", destructive: true),
            StringComparison.Ordinal);

        safety.Gates["redis_del"] = false;
        Assert.Contains(
            "Redis:Gates:redis_del",
            safety.DescribeRefusal("Redis", "redis_del", destructive: true),
            StringComparison.Ordinal);
    }
}
