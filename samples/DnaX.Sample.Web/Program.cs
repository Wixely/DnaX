using DnaX.Caching;
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using DnaX.Diagnostics;
using DnaX.Hosting;
using DnaX.RemoteAccess;
using DnaX.RemoteAccess.Mcp;
using DnaX.RemoteAccess.Sqlite;
using Microsoft.Data.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDnaXHosting(options => options.WritableDataRoot = "data");
builder.Services.AddDnaXCaching();
builder.Services.AddDnaXDataMigrations("Sample", options =>
{
    options.ConnectionFactory = services =>
    {
        string databasePath = services.GetRequiredService<IDnaXPaths>().ResolveWritable("sample.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        return new SqliteConnection($"Data Source={databasePath}");
    };
    options.Manifest = new DnaXMigrationManifest(
        currentVersion: 1,
        migrations:
        [
            DnaXMigration.Sql(1, "create-sample-messages", "Create sample messages", """
                CREATE TABLE SampleMessages (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    Message TEXT NOT NULL
                );
                """)
        ]);
    options.ApplicationVersion = "sample";
    options.UseSqlite();
});
builder.Services.AddDnaXDiagnostics(options =>
{
    options.EnableDetails = true;
    options.IncludeRoutes = true;
    // Demo only. Production diagnostics should keep authorization enabled.
    options.RequireAuthorization = false;
});
builder.Services.AddSingleton<SampleRemoteOperation>();
builder.Services.AddDnaXRemoteAccess(builder.Configuration.GetSection("DnaX:RemoteAccess"));
builder.Services.AddDnaXRemoteAccessSqlite("RemoteAccess", services =>
{
    string databasePath = services.GetRequiredService<IDnaXPaths>().ResolveWritable("remote-access.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    return new SqliteConnection($"Data Source={databasePath}");
});
builder.Services.AddDnaXRemoteMcp().WithTools<SampleRemoteTools>();

WebApplication app = builder.Build();
await app.Services.MigrateDnaXDatabaseAsync("Sample");
await app.Services.MigrateDnaXDatabaseAsync("RemoteAccess");

app.UseDnaXRemoteAccess();
app.UseRouting();

app.MapGet("/", async (IDnaXCache cache, IDnaXPaths paths, CancellationToken cancellationToken) =>
{
    string message = await cache.HitAsync(
        "sample:message",
        _ => new ValueTask<string>("DNA X is running"),
        cancellationToken: cancellationToken);

    return new SampleRootResponse(message, paths.ContentRoot, paths.WritableDataRoot);
});

app.MapGet("/_sample/schema", async (IDnaXDatabaseMigrator migrator, CancellationToken cancellationToken) =>
    await migrator.GetStatusAsync("Sample", cancellationToken));

app.MapDnaXDiagnostics();
app.MapDnaXRemoteApi()
    .MapGet("/status", async (SampleRemoteOperation operation, CancellationToken cancellationToken) =>
        await operation.GetStatusAsync(cancellationToken))
    .WithDnaXRemoteAction("sample.get_status");
app.MapDnaXRemoteMcp();
app.Run();
