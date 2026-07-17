using System.Reflection;
using DnaX.Redis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DnaX.Redis.StackExchangeRedis.Tests;

public sealed class DnaXRedisTests
{
    [Fact]
    public void PassesDatabaseFromNamedMultiplexerWithoutTakingOwnership()
    {
        IDatabase database = DispatchProxy.Create<IDatabase, DatabaseProxy>();
        IConnectionMultiplexer multiplexer = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        ((MultiplexerProxy)(object)multiplexer).Database = database;

        using ServiceProvider provider = CreateProvider(builder => builder.AddRedis("Primary", multiplexer));
        IDnaXRedis redis = provider.GetRequiredService<IDnaXRedis>();

        IDatabase actual = redis.Execute("Primary", value => value);

        Assert.Same(database, actual);
    }

    [Fact]
    public void UnknownNameListsConfiguredResources()
    {
        IConnectionMultiplexer multiplexer = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        using ServiceProvider provider = CreateProvider(builder => builder.AddRedis("Known", multiplexer));
        IDnaXRedis redis = provider.GetRequiredService<IDnaXRedis>();

        DnaXRedisNotFoundException exception = Assert.Throws<DnaXRedisNotFoundException>(
            () => redis.Execute("Missing", _ => 0));

        Assert.Contains("Known", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(Action<DnaXRedisBuilder> configure)
    {
        ServiceCollection services = new();
        services.AddLogging();
        DnaXRedisBuilder builder = services.AddDnaXRedis();
        configure(builder);
        return services.BuildServiceProvider();
    }

    public class DatabaseProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    public class MultiplexerProxy : DispatchProxy
    {
        public IDatabase? Database { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IConnectionMultiplexer.GetDatabase) => Database,
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }
}
