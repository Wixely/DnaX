using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DnaX.Caching;

public static class DnaXCachingExtensions
{
    public static IServiceCollection AddDnaXCaching(
        this IServiceCollection services,
        Action<DnaXCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is null)
        {
            services.AddOptions<DnaXCacheOptions>();
        }
        else
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DnaXCacheRefreshService>();
        services.TryAddSingleton<IDnaXCacheRefreshQueue>(
            static provider => provider.GetRequiredService<DnaXCacheRefreshService>());
        services.TryAddSingleton<IDnaXCache, DnaXCache>();
        services.AddSingleton<IHostedService>(
            static provider => provider.GetRequiredService<DnaXCacheRefreshService>());
        return services;
    }
}
