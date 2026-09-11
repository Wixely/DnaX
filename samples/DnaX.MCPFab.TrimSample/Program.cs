using DnaX.MCPFab;
using DnaX.MCPFab.TrimSample;

// The ~8.6 KB Program.cs that seventeen servers carried, reduced to its three varying parts:
// domain services, the tool list, and the banner/health payload.
McpFabBuilder builder = McpFabHost.CreateBuilder(
    args,
    new McpFabProduct("DnaXMCPFabTrimSample", "TRIMSAMPLE_", DefaultPort: 5798, LogFilePrefix: "trimsample"));

builder.Services.AddSingleton<SampleStore>();

// Registered so the SDK resolves it as a service and excludes it from the tool schema. A
// complex tool PARAMETER that is not a DI service needs a JsonTypeInfo, which does not exist
// once reflection-based serialization is off - the server then dies at startup.
builder.Services.AddSingleton(new McpFabLimitsOptions());

// Generic WithTools<T> only. WithToolsFromAssembly is banned by DnaX.MCPFab.Build precisely
// because it would leave this sample advertising zero tools once trimmed.
builder.Mcp.WithTools<SampleTools>();

builder
    .Banner((services, banner) =>
    {
        SampleStore store = services.GetRequiredService<SampleStore>();
        banner.Line("Items", store.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    })
    .Health(services => McpJson.Object()
        .Set("items", services.GetRequiredService<SampleStore>().Count));

return await builder.Build().RunAsync().ConfigureAwait(false);
