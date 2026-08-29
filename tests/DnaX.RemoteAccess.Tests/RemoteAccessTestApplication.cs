using DnaX.Data.Migrations;
using DnaX.RemoteAccess.Sqlite;
using DnaX.RemoteAccess.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DnaX.RemoteAccess.Tests;

internal sealed class RemoteAccessTestApplication : IAsyncDisposable
{
    private readonly string? _ownedDirectory;

    private RemoteAccessTestApplication(WebApplication application, HttpClient client, string? ownedDirectory, string databasePath)
    {
        Application = application;
        Client = client;
        _ownedDirectory = ownedDirectory;
        DatabasePath = databasePath;
    }

    public WebApplication Application { get; }

    public HttpClient Client { get; }

    public string DatabasePath { get; }

    public IDnaXRemoteAccessAdministration Administration =>
        Application.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();

    public static async Task<RemoteAccessTestApplication> CreateAsync(
        Action<DnaXRemoteAccessOptions>? configure = null,
        string? existingDatabasePath = null)
    {
        string directory = existingDatabasePath is null
            ? Path.Combine(Path.GetTempPath(), "dnax-remote-tests", Guid.NewGuid().ToString("N"))
            : Path.GetDirectoryName(Path.GetFullPath(existingDatabasePath))!;
        Directory.CreateDirectory(directory);
        string databasePath = existingDatabasePath ?? Path.Combine(directory, "remote.db");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddDnaXRemoteAccess(options =>
        {
            options.DeploymentId = "dnax-remote-test";
            options.Network.RequireHttps = false;
            configure?.Invoke(options);
        });
        builder.Services.AddDnaXRemoteAccessSqlite(
            "RemoteAccess",
            _ => new SqliteConnection($"Data Source={databasePath};Pooling=False"));
        builder.Services.AddSingleton<TestRemoteOperation>();
        builder.Services.AddDnaXRemoteMcp().WithTools<TestRemoteTools>();

        WebApplication app = builder.Build();
        await app.Services.MigrateDnaXDatabaseAsync("RemoteAccess");
        app.UseDnaXRemoteAccess();
        app.UseRouting();
        app.MapDnaXRemoteApi()
            .MapGet("/status", (TestRemoteOperation operation) => TypedResults.Ok(operation.GetStatus()))
            .WithDnaXRemoteAction("sample.get_status");
        app.MapDnaXRemoteMcp();
        app.MapGet("/{**path}", static () => TypedResults.Ok(new { fallback = true }));
        await app.StartAsync();

        return new RemoteAccessTestApplication(
            app,
            app.GetTestClient(),
            existingDatabasePath is null ? directory : null,
            databasePath);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Application.DisposeAsync();
        if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
        {
            Directory.Delete(_ownedDirectory, recursive: true);
        }
    }
}

internal sealed class TestRemoteOperation
{
    public int Calls { get; private set; }

    public object GetStatus()
    {
        Calls++;
        return new { status = "ready", calls = Calls };
    }
}

[McpServerToolType]
internal sealed class TestRemoteTools
{
    [McpServerTool(Name = "get_status", UseStructuredContent = true)]
    public static object GetStatus(TestRemoteOperation operation) => operation.GetStatus();
}
