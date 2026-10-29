using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DnaX.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Diagnostics.Tests;

public sealed class DnaXDiagnosticsTests
{
    [Fact]
    public void DefaultsToAuthorizedHealthEndpointsWithoutDetails()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddDnaXDiagnostics();
        WebApplication app = builder.Build();

        app.MapDnaXDiagnostics();
        RouteEndpoint[] endpoints = GetDnaXEndpoints(app);

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/_dna/live");
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/_dna/ready");
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText == "/_dna/info");
        Assert.All(endpoints, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void DetailsAndRoutesAreExplicitOptIns()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddDnaXDiagnostics(options =>
        {
            options.EnableDetails = true;
            options.IncludeRoutes = true;
            options.RequireAuthorization = false;
        });
        WebApplication app = builder.Build();

        app.MapDnaXDiagnostics("/management");
        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/management/info");
    }

    [Fact]
    public async Task ResponsesDoNotDependOnTheHostJsonResolver()
    {
        // A NativeAOT host only resolves the types it registered itself. Model that with an
        // empty resolver: every payload must come from the library's own generated context.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(static options =>
            options.SerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine());
        builder.Services.AddDnaXDiagnostics(static options =>
        {
            options.EnableDetails = true;
            options.RequireAuthorization = false;
        });
        await using WebApplication app = builder.Build();
        app.MapDnaXDiagnostics();
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();

        JsonElement live = await client.GetFromJsonAsync<JsonElement>("/_dna/live");
        Assert.Equal("Healthy", live.GetProperty("status").GetString());

        JsonElement ready = await client.GetFromJsonAsync<JsonElement>("/_dna/ready");
        Assert.Equal("Healthy", ready.GetProperty("Status").GetString());

        JsonElement info = await client.GetFromJsonAsync<JsonElement>("/_dna/info");
        Assert.True(info.TryGetProperty("application", out _));
        Assert.True(info.GetProperty("threadPool").TryGetProperty("availableWorkers", out _));
    }

    private static RouteEndpoint[] GetDnaXEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/_dna", StringComparison.Ordinal) == true)
            .ToArray();
}
