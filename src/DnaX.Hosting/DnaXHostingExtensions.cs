using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Hosting;

public static class DnaXHostingExtensions
{
    public static IServiceCollection AddDnaXHosting(
        this IServiceCollection services,
        Action<DnaXPathOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is null)
        {
            services.AddOptions<DnaXPathOptions>();
        }
        else
        {
            services.Configure(configure);
        }

        services.AddSingleton<IDnaXPaths, DnaXPaths>();
        return services;
    }
}
