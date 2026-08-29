using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.RemoteAccess.Tests;

public sealed class DnaXRemoteAccessTests
{
    [Fact]
    public async Task DefaultsExposeNoControlsOrRoutes()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync();

        DnaXRemoteAdministrationState state = await test.Administration.GetStateAsync();
        HttpResponseMessage response = await test.Client.GetAsync("/api/v1/status");

        Assert.False(state.Enabled);
        Assert.Equal(DnaXRemoteAvailability.Disabled, state.Api.Availability);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FixedApiRequiresBothGatesAndBearerCredential()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableFixedApi);
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api,
            expectedVersion: 0,
            scopes: ["sample.read"]);
        DnaXRemoteEffectiveSurface active = await test.Administration.SetActivationAsync(
            DnaXRemoteSurface.Api,
            active: true,
            allowAnonymous: false,
            expectedVersion: credential.Version);

        Assert.Equal("/api/v1", active.EndpointPath);
        Assert.Equal(HttpStatusCode.Unauthorized, (await test.Client.GetAsync("/api/v1/status")).StatusCode);

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/status");
        request.Headers.Authorization = new("Bearer", credential.Secret);
        HttpResponseMessage response = await test.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CredentialIsHashedAndRotationImmediatelyRejectsThePreviousSecret()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableFixedApi);
        DnaXGeneratedCredential first = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, first.Version);
        DnaXRemoteAdministrationState beforeRotation = await test.Administration.GetStateAsync();
        DnaXGeneratedCredential second = await test.Administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api,
            beforeRotation.Api.Version);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(test, "/api/v1/status", first.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(test, "/api/v1/status", second.Secret)).StatusCode);

        string database = await File.ReadAllTextAsync(test.DatabasePath);
        Assert.DoesNotContain(first.Secret, database, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Secret, database, StringComparison.Ordinal);
        Assert.Contains(second.Suffix, database, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RandomizedEndpointRotationInvalidatesTheOldRouteImmediately()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableRandomApi);
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        DnaXRemoteEffectiveSurface first = await test.Administration.SetActivationAsync(
            DnaXRemoteSurface.Api,
            true,
            false,
            credential.Version);
        DnaXRemoteEffectiveSurface second = await test.Administration.RotateEndpointAsync(
            DnaXRemoteSurface.Api,
            first.Version);

        Assert.NotEqual(first.EndpointPath, second.EndpointPath);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(test, first.EndpointPath + "/status", credential.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(test, second.EndpointPath + "/status", credential.Secret)).StatusCode);
    }

    [Fact]
    public async Task AnonymousMcpRequiresDeploymentAndAdministratorOptIn()
    {
        await using RemoteAccessTestApplication forbidden = await RemoteAccessTestApplication.CreateAsync(options =>
        {
            EnableMcp(options, allowAnonymous: false);
        });
        await Assert.ThrowsAsync<DnaXRemotePolicyException>(async () =>
            await forbidden.Administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, true, 0));

        await using RemoteAccessTestApplication allowed = await RemoteAccessTestApplication.CreateAsync(options =>
        {
            EnableMcp(options, allowAnonymous: true);
        });
        DnaXRemoteEffectiveSurface state = await allowed.Administration.SetActivationAsync(
            DnaXRemoteSurface.Mcp,
            true,
            true,
            0);

        HttpResponseMessage response = await SendMcpAsync(allowed, state.EndpointPath!, secret: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OptimisticConcurrencyRejectsStaleAdministrationWrites()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableFixedApi);
        await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);

        await Assert.ThrowsAsync<DnaXRemoteConcurrencyException>(async () =>
            await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0));
    }

    [Fact]
    public async Task RevocationImmediatelyRejectsTheCredential()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableFixedApi);
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        DnaXRemoteEffectiveSurface active = await test.Administration.SetActivationAsync(
            DnaXRemoteSurface.Api,
            true,
            false,
            credential.Version);

        await test.Administration.RevokeCredentialAsync(DnaXRemoteSurface.Api, active.Version);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(test, "/api/v1/status", credential.Secret)).StatusCode);
    }

    [Fact]
    public async Task StateFromAnotherDeploymentFailsClosed()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableFixedApi);
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, credential.Version);
        await using (SqliteConnection connection = new($"Data Source={test.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE DnaXRemoteSurfaces SET DeploymentId = 'different-deployment';";
            await command.ExecuteNonQueryAsync();
        }

        DnaXRemoteAdministrationState state = await test.Administration.GetStateAsync();

        Assert.Equal(DnaXRemoteAvailability.UnavailableByDeploymentPolicy, state.Api.Availability);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(test, "/api/v1/status", credential.Secret)).StatusCode);
    }

    [Fact]
    public async Task RestoredEnabledDatabaseCannotOverrideDisabledDeploymentPolicy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dnax-remote-restore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "restored.db");
        try
        {
            DnaXGeneratedCredential credential;
            await using (RemoteAccessTestApplication source = await RemoteAccessTestApplication.CreateAsync(
                EnableFixedApi,
                databasePath))
            {
                credential = await source.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
                await source.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, credential.Version);
            }

            await using RemoteAccessTestApplication restored = await RemoteAccessTestApplication.CreateAsync(
                options =>
                {
                    options.Enabled = false;
                    options.Api.Available = false;
                    options.Api.UseRandomizedEndpoint = false;
                },
                databasePath);
            DnaXRemoteAdministrationState state = await restored.Administration.GetStateAsync();

            Assert.Equal(DnaXRemoteAvailability.Disabled, state.Api.Availability);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(restored, "/api/v1/status", credential.Secret)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AuditUsesRedactedActionMetadataAndNeverStoresSecretRouteOrQuery()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(EnableRandomApi);
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        DnaXRemoteEffectiveSurface active = await test.Administration.SetActivationAsync(
            DnaXRemoteSurface.Api,
            true,
            false,
            credential.Version);
        string sensitiveQuery = "private-search-value";

        await SendAsync(test, active.EndpointPath + "/status?q=" + sensitiveQuery, credential.Secret);
        DnaXRemoteAuditRecord record = Assert.Single(await test.Administration.GetRecentActivityAsync(10));

        Assert.Equal("sample.get_status", record.Event.Action);
        string database = await File.ReadAllTextAsync(test.DatabasePath);
        Assert.DoesNotContain(credential.Secret, database, StringComparison.Ordinal);
        Assert.DoesNotContain(active.EndpointPath!, database, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveQuery, database, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountRetentionIsDeterministic()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(options =>
        {
            EnableFixedApi(options);
            options.Audit.MaximumEvents = 2;
        });
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, credential.Version);

        await SendAsync(test, "/api/v1/status", credential.Secret);
        await SendAsync(test, "/api/v1/status", credential.Secret);
        await SendAsync(test, "/api/v1/status", credential.Secret);

        Assert.Equal(2, (await test.Administration.GetRecentActivityAsync(10)).Count);
    }

    [Fact]
    public async Task RequestSizeAndRateLimitsAreEnforced()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(options =>
        {
            EnableFixedApi(options);
            options.Limits.MaximumRequestBodyBytes = 4;
            options.Limits.RequestsPerMinutePerIdentity = 1;
        });
        DnaXGeneratedCredential credential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, credential.Version);

        using HttpRequestMessage oversized = new(HttpMethod.Post, "/api/v1/status");
        oversized.Headers.Authorization = new("Bearer", credential.Secret);
        oversized.Content = new StringContent("12345", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await test.Client.SendAsync(oversized)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(test, "/api/v1/status", credential.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(test, "/api/v1/status", credential.Secret)).StatusCode);
    }

    [Fact]
    public async Task ApiAndMcpInvokeTheSameApplicationOperation()
    {
        await using RemoteAccessTestApplication test = await RemoteAccessTestApplication.CreateAsync(options =>
        {
            EnableFixedApi(options);
            EnableMcp(options, allowAnonymous: false);
            options.Mcp.UseRandomizedEndpoint = false;
        });
        DnaXGeneratedCredential apiCredential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Api, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Api, true, false, apiCredential.Version);
        DnaXGeneratedCredential mcpCredential = await test.Administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0);
        await test.Administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, mcpCredential.Version);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(test, "/api/v1/status", apiCredential.Secret)).StatusCode);

        HttpResponseMessage response = await SendMcpAsync(test, "/mcp", mcpCredential.Secret);
        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("ready", body, StringComparison.Ordinal);
        Assert.Equal(2, test.Application.Services.GetRequiredService<TestRemoteOperation>().Calls);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        RemoteAccessTestApplication test,
        string path,
        string secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", secret);
        return await test.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMcpAsync(
        RemoteAccessTestApplication test,
        string path,
        string? secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        if (secret is not null)
        {
            request.Headers.Authorization = new("Bearer", secret);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "get_status");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
            Encoding.UTF8,
            "application/json");
        return await test.Client.SendAsync(request);
    }

    private static void EnableFixedApi(DnaXRemoteAccessOptions options)
    {
        options.Enabled = true;
        options.Api.Available = true;
        options.Api.UseRandomizedEndpoint = false;
        options.Api.AllowRuntimeActivation = true;
        options.Api.AllowCredentialRotation = true;
        options.Api.AllowEndpointRotation = false;
    }

    private static void EnableRandomApi(DnaXRemoteAccessOptions options)
    {
        EnableFixedApi(options);
        options.Api.UseRandomizedEndpoint = true;
        options.Api.AllowEndpointRotation = true;
    }

    private static void EnableMcp(DnaXRemoteAccessOptions options, bool allowAnonymous)
    {
        options.Enabled = true;
        options.Mcp.Available = true;
        options.Mcp.UseRandomizedEndpoint = true;
        options.Mcp.AllowRuntimeActivation = true;
        options.Mcp.AllowCredentialRotation = true;
        options.Mcp.AllowEndpointRotation = true;
        options.Mcp.AllowAnonymous = allowAnonymous;
    }
}
