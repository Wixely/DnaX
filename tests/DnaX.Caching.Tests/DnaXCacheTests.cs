using DnaX.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DnaX.Caching.Tests;

public sealed class DnaXCacheTests
{
    [Fact]
    public async Task AsyncMissIsSingleFlight()
    {
        MutableTimeProvider clock = new();
        using IHost host = await CreateHostAsync(clock);
        IDnaXCache cache = host.Services.GetRequiredService<IDnaXCache>();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<int>[] requests = Enumerable.Range(0, 20)
            .Select(_ => cache.HitAsync(
                "single-flight",
                async cancellationToken =>
                {
                    Interlocked.Increment(ref calls);
                    await release.Task.WaitAsync(cancellationToken);
                    return 7;
                }).AsTask())
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        release.SetResult();

        Assert.All(await Task.WhenAll(requests), value => Assert.Equal(7, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpirationStartsAfterSlowFactoryCompletes()
    {
        MutableTimeProvider clock = new();
        using IHost host = await CreateHostAsync(clock);
        IDnaXCache cache = host.Services.GetRequiredService<IDnaXCache>();
        int calls = 0;
        DnaXCacheEntryOptions options = new() { Duration = TimeSpan.FromMinutes(1) };

        int first = await cache.HitAsync(
            "slow",
            _ =>
            {
                calls++;
                clock.Advance(TimeSpan.FromMinutes(2));
                return new ValueTask<int>(3);
            },
            options);
        int second = cache.Hit("slow", () => ++calls, options);

        Assert.Equal(3, first);
        Assert.Equal(3, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NullCanBeExcludedFromCache()
    {
        MutableTimeProvider clock = new();
        using IHost host = await CreateHostAsync(clock);
        IDnaXCache cache = host.Services.GetRequiredService<IDnaXCache>();
        int calls = 0;
        DnaXCacheEntryOptions options = new() { CacheNulls = false };

        _ = cache.Hit<string?>("nullable", () => { calls++; return null; }, options);
        _ = cache.Hit<string?>("nullable", () => { calls++; return null; }, options);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReusingKeyWithDifferentTypeIsRejected()
    {
        MutableTimeProvider clock = new();
        using IHost host = await CreateHostAsync(clock);
        IDnaXCache cache = host.Services.GetRequiredService<IDnaXCache>();

        _ = cache.Hit("typed", () => 1);

        Assert.Throws<DnaXCacheTypeMismatchException>(() => cache.Hit("typed", () => "one"));
    }

    [Fact]
    public async Task ServesStaleValueWhileOneBackgroundRefreshRuns()
    {
        MutableTimeProvider clock = new();
        using IHost host = await CreateHostAsync(clock);
        IDnaXCache cache = host.Services.GetRequiredService<IDnaXCache>();
        DnaXCacheEntryOptions options = new()
        {
            Duration = TimeSpan.FromMinutes(1),
            StaleDuration = TimeSpan.FromMinutes(5)
        };

        int calls = 0;
        Assert.Equal(1, cache.Hit("stale", () => Interlocked.Increment(ref calls), options));
        clock.Advance(TimeSpan.FromMinutes(2));

        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int stale = await cache.HitAsync(
            "stale",
            _ =>
            {
                int value = Interlocked.Increment(ref calls);
                refreshed.TrySetResult();
                return new ValueTask<int>(value);
            },
            options);

        Assert.Equal(1, stale);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => cache.Hit("stale", () => 99, options) == 2);
        Assert.Equal(2, calls);
    }

    private static async ValueTask<IHost> CreateHostAsync(TimeProvider timeProvider)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddDnaXCaching();
        IHost host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
