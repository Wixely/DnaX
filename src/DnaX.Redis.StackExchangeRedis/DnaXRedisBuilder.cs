using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DnaX.Redis;

public sealed class DnaXRedisBuilder
{
    internal DnaXRedisBuilder(IServiceCollection services) => Services = services;

    internal IServiceCollection Services { get; }

    public DnaXRedisBuilder AddRedis(string name, string configuration, int database = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ConfigurationOptions options = ConfigurationOptions.Parse(configuration);
        return AddRedis(
            name,
            async _ => await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false),
            database,
            ownsMultiplexer: true);
    }

    public DnaXRedisBuilder AddRedis(
        string name,
        ConfigurationOptions configuration,
        int database = -1)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddRedis(
            name,
            async _ => await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false),
            database,
            ownsMultiplexer: true);
    }

    public DnaXRedisBuilder AddRedis(
        string name,
        IConnectionMultiplexer multiplexer,
        int database = -1)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        return AddRedis(
            name,
            _ => new ValueTask<IConnectionMultiplexer>(multiplexer),
            database,
            ownsMultiplexer: false);
    }

    public DnaXRedisBuilder AddRedis(
        string name,
        Func<IServiceProvider, ValueTask<IConnectionMultiplexer>> multiplexerFactory,
        int database = -1,
        bool ownsMultiplexer = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(multiplexerFactory);
        if (database < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(database));
        }

        Services.AddSingleton(new DnaXRedisRegistration(
            name,
            multiplexerFactory,
            database,
            ownsMultiplexer));
        return this;
    }
}

internal sealed record DnaXRedisRegistration(
    string Name,
    Func<IServiceProvider, ValueTask<IConnectionMultiplexer>> MultiplexerFactory,
    int Database,
    bool OwnsMultiplexer);
