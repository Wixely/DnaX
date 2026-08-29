using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DnaX.RemoteAccess.Mcp;

public static class DnaXRemoteMcpExtensions
{
    private const string InternalMcpPath = "/_dnax/remote/mcp";

    public static IMcpServerBuilder AddDnaXRemoteMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true);
    }

    public static IEndpointConventionBuilder MapDnaXRemoteMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        IEndpointConventionBuilder builder = endpoints.MapMcp(InternalMcpPath);
        builder.DisableAntiforgery();
        builder.WithMetadata(new DnaXRemoteActionAttribute("mcp.jsonrpc"));
        return builder;
    }
}
