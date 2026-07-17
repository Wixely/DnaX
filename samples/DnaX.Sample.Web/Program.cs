using DnaX.Caching;
using DnaX.Diagnostics;
using DnaX.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDnaXHosting(options => options.WritableDataRoot = "data");
builder.Services.AddDnaXCaching();
builder.Services.AddDnaXDiagnostics(options =>
{
    options.EnableDetails = true;
    options.IncludeRoutes = true;
    // Demo only. Production diagnostics should keep authorization enabled.
    options.RequireAuthorization = false;
});

WebApplication app = builder.Build();

app.MapGet("/", async (IDnaXCache cache, IDnaXPaths paths, CancellationToken cancellationToken) =>
{
    string message = await cache.HitAsync(
        "sample:message",
        _ => new ValueTask<string>("DNA X is running"),
        cancellationToken: cancellationToken);

    return new { message, paths.ContentRoot, paths.WritableDataRoot };
});

app.MapDnaXDiagnostics();
app.Run();
