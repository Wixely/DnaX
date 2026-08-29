using System.Data.Common;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DnaX.RemoteAccess.Sqlite;

public static class DnaXRemoteAccessSqliteExtensions
{
    public static IServiceCollection AddDnaXRemoteAccessSqlite(
        this IServiceCollection services,
        string databaseName,
        Func<IServiceProvider, DbConnection> connectionFactory,
        bool migrateOnStartup = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        services.AddDnaXDataMigrations(databaseName, options =>
        {
            options.ConnectionFactory = connectionFactory;
            options.Manifest = DnaXRemoteAccessSqliteSchema.Manifest;
            options.ApplicationVersion = typeof(DnaXRemoteAccessSqliteExtensions).Assembly.GetName().Version?.ToString();
            options.MigrateOnStartup = migrateOnStartup;
            options.UseSqlite();
        });
        services.TryAddSingleton<IDnaXRemoteAccessStore>(provider =>
            new DnaXRemoteAccessSqliteStore(provider, connectionFactory));
        return services;
    }
}
