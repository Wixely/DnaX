using System.Data.Common;

namespace DnaX.Data.Migrations;

public sealed class DnaXMigrationOptions
{
    public Func<IServiceProvider, DbConnection>? ConnectionFactory { get; set; }

    public DnaXMigrationManifest? Manifest { get; set; }

    public IDnaXMigrationAdapter? Adapter { get; set; }

    public string? ApplicationVersion { get; set; }

    public bool MigrateOnStartup { get; set; }

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public Func<DnaXBeforeMigrationContext, CancellationToken, ValueTask>? BeforeMigrateAsync { get; set; }
}

internal sealed record DnaXMigrationRegistration(
    string Name,
    Func<IServiceProvider, DbConnection> ConnectionFactory,
    DnaXMigrationManifest Manifest,
    IDnaXMigrationAdapter Adapter,
    string? ApplicationVersion,
    bool MigrateOnStartup,
    TimeProvider TimeProvider,
    Func<DnaXBeforeMigrationContext, CancellationToken, ValueTask>? BeforeMigrateAsync);
