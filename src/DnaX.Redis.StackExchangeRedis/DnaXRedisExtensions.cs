using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DnaX.Redis;

public static class DnaXRedisExtensions
{
    public static DnaXRedisBuilder AddDnaXRedis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDnaXRedis, DnaXRedis>();
        return new DnaXRedisBuilder(services);
    }
}
