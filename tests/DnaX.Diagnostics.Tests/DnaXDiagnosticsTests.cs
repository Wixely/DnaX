using DnaX.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

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

    private static RouteEndpoint[] GetDnaXEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/_dna", StringComparison.Ordinal) == true)
            .ToArray();
}
