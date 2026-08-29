using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnaX.RemoteAccess;

public static class DnaXRemoteAccessExtensions
{
    internal const string InternalApiPrefix = "/_dnax/remote/api/v1";
    internal const string InternalMcpPath = "/_dnax/remote/mcp";

    public static IServiceCollection AddDnaXRemoteAccess(
        this IServiceCollection services,
        Action<DnaXRemoteAccessOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDataProtection();
        services.AddAuthorization();
        services.AddOptions<DnaXRemoteAccessOptions>()
            .Validate(ValidateOptions, "Invalid DNA X remote-access configuration.")
            .ValidateOnStart();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DnaXRemoteAccessService>();
        services.TryAddSingleton<IDnaXRemoteAccessAdministration>(static provider =>
            provider.GetRequiredService<DnaXRemoteAccessService>());
        services.TryAddSingleton<IDnaXRemoteAccessDiagnostics>(static provider =>
            provider.GetRequiredService<DnaXRemoteAccessService>());
        services.TryAddSingleton<IDnaXRemoteAccessRuntime>(static provider =>
            provider.GetRequiredService<DnaXRemoteAccessService>());
        services.TryAddSingleton<DnaXRemoteRequestLimiter>();
        return services;
    }

    public static IServiceCollection AddDnaXRemoteAccess(
        this IServiceCollection services,
        IConfigurationSection configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddDnaXRemoteAccess();
        services.AddOptions<DnaXRemoteAccessOptions>().Bind(configuration);
        return services;
    }

    public static IApplicationBuilder UseDnaXRemoteAccess(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DnaXRemoteAccessMiddleware>();
    }

    public static RouteGroupBuilder MapDnaXRemoteApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGroup(InternalApiPrefix);
    }

    public static TBuilder WithDnaXRemoteAction<TBuilder>(this TBuilder builder, string action)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new DnaXRemoteActionAttribute(action));
        return builder;
    }

    private static bool ValidateOptions(DnaXRemoteAccessOptions options)
    {
        if (options.Enabled && string.IsNullOrWhiteSpace(options.DeploymentId))
        {
            return false;
        }

        if (!ValidPath(options.Api.FixedEndpointPath) || !ValidPath(options.Mcp.FixedEndpointPath))
        {
            return false;
        }

        if (string.Equals(options.Api.FixedEndpointPath, options.Mcp.FixedEndpointPath, StringComparison.OrdinalIgnoreCase) ||
            options.Api.FixedEndpointPath.StartsWith("/_dnax", StringComparison.OrdinalIgnoreCase) ||
            options.Mcp.FixedEndpointPath.StartsWith("/_dnax", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return options.Audit.Retention > TimeSpan.Zero &&
               options.Audit.MaximumEvents > 0 &&
               options.Limits.MaximumRequestBodyBytes > 0 &&
               options.Limits.MaximumConcurrentRequestsPerSurface > 0 &&
               options.Limits.RequestTimeout > TimeSpan.Zero &&
               options.Limits.MaximumPageSize > 0 &&
               options.Limits.MaximumResultCount >= options.Limits.MaximumPageSize &&
               options.Limits.RequestsPerMinutePerIdentity > 0;
    }

    private static bool ValidPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path[0] == '/' &&
        path.Length > 1 &&
        !path.EndsWith("/", StringComparison.Ordinal) &&
        !path.Contains('?', StringComparison.Ordinal) &&
        !path.Contains('#', StringComparison.Ordinal);
}
