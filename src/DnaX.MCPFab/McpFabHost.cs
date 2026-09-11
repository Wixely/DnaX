using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DnaX.MCPFab;

/// <summary>
/// Entry point for an MCPFab server, replacing the ~8.6 KB <c>Program.cs</c> that seventeen
/// servers carried in near-identical form.
/// </summary>
public static class McpFabHost
{
    /// <summary>
    /// Starts building a server. Everything invariant across the estate is applied here; the
    /// caller supplies only domain services, the tool list, the banner and the health payload.
    /// </summary>
    public static McpFabBuilder CreateBuilder(string[] args, McpFabProduct product)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(product);

        string contentRoot = McpFabConfiguration.GetContentRoot();
        bool isWindowsService = WindowsServiceHelpers.IsWindowsService();

        // Anything thrown before the real logger exists would otherwise be invisible.
        Log.Logger = McpFabLogging.CreateBootstrapLogger(product, contentRoot);

        WebApplicationBuilder web = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
        });

        web.Configuration.AddMcpFabSources(product, contentRoot, web.Environment.EnvironmentName, args);

        McpFabLoggingOptions logging = new();
        IConfigurationSection loggingSection = web.Configuration.GetSection(McpFabLoggingOptions.SectionName);
        logging.MinimumLevel = loggingSection["MinimumLevel:Default"] ?? logging.MinimumLevel;
        foreach (IConfigurationSection entry in loggingSection.GetSection("MinimumLevel:Override").GetChildren())
        {
            if (entry.Value is { Length: > 0 } level)
            {
                logging.Override[entry.Key] = level;
            }
        }

        loggingSection.GetSection("Console").Bind(logging.Console);
        loggingSection.GetSection("File").Bind(logging.File);

        web.Host.UseSerilog((_, services, configuration) => configuration
            .Configure(logging, product, contentRoot)
            .ReadFrom.Services(services));

        web.Services
            .AddOptions<ServerOptions>()
            .Bind(web.Configuration.GetSection(ServerOptions.SectionName))
            .Configure(options =>
            {
                if (options.Port == 0)
                {
                    options.Port = product.DefaultPort;
                }

                if (string.IsNullOrWhiteSpace(options.Path))
                {
                    options.Path = product.DefaultPath;
                }

                if (string.IsNullOrWhiteSpace(options.WindowsServiceName))
                {
                    options.WindowsServiceName = product.Name;
                }
            })
            .ValidateOnStart();

        web.Services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        web.Services.AddSingleton(product);

        ServerOptions server = ResolveServerOptions(web.Configuration, product);

        if (isWindowsService)
        {
            web.Host.UseWindowsService(options => options.ServiceName = server.WindowsServiceName);
        }

        web.WebHost.ConfigureKestrel(kestrel =>
        {
            if (IPAddress.TryParse(server.Host, out IPAddress? address))
            {
                kestrel.Listen(address, server.Port);
            }
            else if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                kestrel.ListenLocalhost(server.Port);
            }
            else
            {
                kestrel.ListenAnyIP(server.Port);
            }
        });

        web.Services
            .AddAuthentication(McpFabAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, McpFabPasswordAuthenticationHandler>(
                McpFabAuthentication.SchemeName,
                configureOptions: null);
        web.Services.AddAuthorization();

        IMcpServerBuilder mcp = web.Services.AddMcpServer().WithHttpTransport();

        return new McpFabBuilder(web, mcp, product, server, contentRoot, isWindowsService);
    }

    /// <summary>
    /// Reads the server section eagerly for the values needed before the container exists
    /// (Kestrel binding, service name, the endpoint path passed to MapMcp).
    /// </summary>
    private static ServerOptions ResolveServerOptions(IConfiguration configuration, McpFabProduct product)
    {
        ServerOptions server = new();
        configuration.GetSection(ServerOptions.SectionName).Bind(server);

        if (server.Port == 0)
        {
            server.Port = product.DefaultPort;
        }

        if (string.IsNullOrWhiteSpace(server.Path))
        {
            server.Path = product.DefaultPath;
        }

        if (string.IsNullOrWhiteSpace(server.WindowsServiceName))
        {
            server.WindowsServiceName = product.Name;
        }

        return server;
    }
}

/// <summary>Configures an MCPFab server before it is built.</summary>
public sealed class McpFabBuilder
{
    private Action<IServiceProvider, IMcpFabBanner>? _banner;
    private Func<IServiceProvider, JsonObject>? _health;

    internal McpFabBuilder(
        WebApplicationBuilder web,
        IMcpServerBuilder mcp,
        McpFabProduct product,
        ServerOptions server,
        string contentRoot,
        bool isWindowsService)
    {
        Web = web;
        Mcp = mcp;
        Product = product;
        Server = server;
        ContentRoot = contentRoot;
        IsWindowsService = isWindowsService;
    }

    /// <summary>The underlying builder, for anything MCPFab does not wrap.</summary>
    public WebApplicationBuilder Web { get; }

    /// <summary>Domain service registrations go here.</summary>
    public IServiceCollection Services => Web.Services;

    /// <summary>Configuration with the standard source chain already applied.</summary>
    public IConfiguration Configuration => Web.Configuration;

    /// <summary>
    /// MCP registration. Use the generic <c>WithTools&lt;T&gt;()</c>: the assembly-scanning
    /// overloads are trim-unsafe and leave a trimmed server advertising zero tools.
    /// </summary>
    public IMcpServerBuilder Mcp { get; }

    /// <summary>This server's identity.</summary>
    public McpFabProduct Product { get; }

    /// <summary>Server options as resolved at startup.</summary>
    public ServerOptions Server { get; }

    /// <summary>Directory holding the executable, config and logs.</summary>
    public string ContentRoot { get; }

    /// <summary>Whether the Windows Service Control Manager started this process.</summary>
    public bool IsWindowsService { get; }

    /// <summary>
    /// Supplies the startup banner. Takes the built container because every server computed its
    /// banner from resolved services.
    /// </summary>
    public McpFabBuilder Banner(Action<IServiceProvider, IMcpFabBanner> write)
    {
        _banner = write;
        return this;
    }

    /// <summary>
    /// Supplies the <c>/healthz</c> payload. Returns a <see cref="JsonObject"/> rather than an
    /// anonymous type, which would serialise to <c>{}</c> once trimmed.
    /// </summary>
    public McpFabBuilder Health(Func<IServiceProvider, JsonObject> payload)
    {
        _health = payload;
        return this;
    }

    /// <summary>Builds the application and applies the standard pipeline.</summary>
    public McpFabApp Build()
    {
        WebApplication app = Web.Build();

        app.UseSerilogRequestLogging();

        // Registered after Build so a failure inside the host is still attributed.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        app.UseAuthentication();
        app.UseAuthorization();

        // Health stays anonymous, as it was in every server: MCPHub probes it with no credentials
        // and treats a non-2xx as unhealthy.
        // An explicit RequestDelegate rather than MapGet(Delegate): the delegate-based overloads
        // reflect over parameters and the return type, which is trim-unsafe, and an anonymous
        // return type would serialise to {} once trimmed rather than failing loudly.
        app.MapMethods("/healthz", ["GET"], (RequestDelegate)(context =>
        {
            JsonObject payload = _health?.Invoke(context.RequestServices) ?? McpJson.Object();
            payload.Set("status", "ok");
            payload.Set("server", Product.Name);
            payload.Set("timeUtc", DateTimeOffset.UtcNow);

            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(payload.ToJsonString());
        }));

        app.MapMcp(Server.Path).RequireAuthorization();

        return new McpFabApp(app, Product, Server, ContentRoot, IsWindowsService, _banner);
    }
}
