using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data.Migrations;

public sealed class DnaXMigrationBuilder
{
    internal DnaXMigrationBuilder(IServiceCollection services) => Services = services;

    internal IServiceCollection Services { get; }

    public DnaXMigrationBuilder AddDatabase(string name, Action<DnaXMigrationOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        DnaXMigrationOptions options = new();
        configure(options);

        if (options.ConnectionFactory is null)
        {
            throw new InvalidOperationException($"Migration database '{name}' requires a connection factory.");
        }

        if (options.Manifest is null)
        {
            throw new InvalidOperationException($"Migration database '{name}' requires a manifest.");
        }

        if (options.Adapter is null)
        {
            throw new InvalidOperationException($"Migration database '{name}' requires a provider adapter.");
        }

        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        Services.AddSingleton(new DnaXMigrationRegistration(
            name,
            options.ConnectionFactory,
            options.Manifest,
            options.Adapter,
            options.ApplicationVersion,
            options.MigrateOnStartup,
            options.TimeProvider,
            options.BeforeMigrateAsync));
        return this;
    }
}
