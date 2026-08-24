using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DnaX.Data.Migrations;

public static class DnaXMigrationExtensions
{
    public static DnaXMigrationBuilder AddDnaXDataMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDnaXDatabaseMigrator, DnaXDatabaseMigrator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DnaXMigrationHostedService>());
        return new DnaXMigrationBuilder(services);
    }

    public static DnaXMigrationBuilder AddDnaXDataMigrations(
        this IServiceCollection services,
        string name,
        Action<DnaXMigrationOptions> configure) =>
        services.AddDnaXDataMigrations().AddDatabase(name, configure);

    public static ValueTask<DnaXMigrationResult> MigrateDnaXDatabaseAsync(
        this IServiceProvider services,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IDnaXDatabaseMigrator>().MigrateAsync(name, cancellationToken);
    }

    public static ValueTask<DnaXMigrationBaselineResult> BaselineDnaXDatabaseAsync(
        this IServiceProvider services,
        string name,
        int throughVersion,
        Func<DnaXBaselineVerificationContext, CancellationToken, ValueTask> verifyExistingSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IDnaXDatabaseMigrator>()
            .BaselineAsync(name, throughVersion, verifyExistingSchema, cancellationToken);
    }
}
