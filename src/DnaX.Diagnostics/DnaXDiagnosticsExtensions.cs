using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DnaX.Diagnostics;

public static class DnaXDiagnosticsExtensions
{
    public static IServiceCollection AddDnaXDiagnostics(
        this IServiceCollection services,
        Action<DnaXDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHealthChecks();
        services.AddAuthorization();

        if (configure is null)
        {
            services.AddOptions<DnaXDiagnosticsOptions>();
        }
        else
        {
            services.Configure(configure);
        }

        return services;
    }

    public static RouteGroupBuilder MapDnaXDiagnostics(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/_dna")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        DnaXDiagnosticsOptions options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<DnaXDiagnosticsOptions>>()
            .Value;

        RouteGroupBuilder group = endpoints.MapGroup(prefix);
        if (options.RequireAuthorization)
        {
            if (string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
            {
                group.RequireAuthorization();
            }
            else
            {
                group.RequireAuthorization(options.AuthorizationPolicy);
            }
        }

        group.MapGet("/live", static () => TypedResults.Ok(new DnaXLiveResponse("Healthy")))
            .WithName("DnaXLiveness");

        group.MapGet(
                "/ready",
                async (HealthCheckService checks, CancellationToken cancellationToken) =>
                {
                    HealthReport report = await checks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                    DnaXHealthResponse response = new(
                        report.Status.ToString(),
                        report.TotalDuration,
                        report.Entries.Select(static pair => new DnaXHealthEntry(
                            pair.Key,
                            pair.Value.Status.ToString(),
                            pair.Value.Duration,
                            pair.Value.Description)).ToArray());

                    int statusCode = report.Status == HealthStatus.Unhealthy
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK;
                    return Results.Json(
                        response,
                        DnaXDiagnosticsJsonContext.Default.DnaXHealthResponse,
                        statusCode: statusCode);
                })
            .WithName("DnaXReadiness");

        if (options.EnableDetails)
        {
            group.MapGet(
                    "/info",
                    (IHostEnvironment environment, EndpointDataSource endpointDataSource) =>
                        TypedResults.Ok(CreateRuntimeInfo(environment, endpointDataSource, options.IncludeRoutes)))
                .WithName("DnaXRuntimeInfo");
        }

        return group;
    }

    private static DnaXRuntimeInfo CreateRuntimeInfo(
        IHostEnvironment environment,
        EndpointDataSource endpointDataSource,
        bool includeRoutes)
    {
        ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out int maximumIo);
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);

        Assembly entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using Process process = Process.GetCurrentProcess();
        IReadOnlyList<string>? routes = includeRoutes
            ? endpointDataSource.Endpoints
                .OfType<RouteEndpoint>()
                .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                .Where(static route => route.Length > 0)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : null;

        return new DnaXRuntimeInfo(
            environment.ApplicationName,
            entryAssembly.GetName().Version?.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            environment.EnvironmentName,
            Environment.ProcessorCount,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime(),
            new DnaXThreadPoolInfo(
                availableWorkers,
                availableIo,
                maximumWorkers,
                maximumIo,
                minimumWorkers,
                minimumIo),
            routes);
    }
}
