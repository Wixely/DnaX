using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DnaX.Data.Migrations;

internal sealed class DnaXMigrationHostedService(
    IEnumerable<DnaXMigrationRegistration> registrations,
    IDnaXDatabaseMigrator migrator,
    ILogger<DnaXMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (DnaXMigrationRegistration registration in registrations.Where(item => item.MigrateOnStartup))
        {
            logger.LogInformation("Running startup migrations for database {DatabaseName}.", registration.Name);
            await migrator.MigrateAsync(registration.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
