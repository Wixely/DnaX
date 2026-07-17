using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DnaX.Data;

public static class DnaXDataExtensions
{
    public static DnaXDataBuilder AddDnaXData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDnaXDatabases, DnaXDatabases>();
        return new DnaXDataBuilder(services);
    }
}
