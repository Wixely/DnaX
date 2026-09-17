using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DnaX.Uploads;

public static class UploadEndpoints
{
    public static IServiceCollection AddDnaXUploads(this IServiceCollection services, Action<UploadOptions> configure)
    {
        var options = new UploadOptions(); configure(options);
        services.AddSingleton(_ => new DiskUploadStore(options));
        services.AddAntiforgery(o => o.HeaderName = "X-DnaX-Antiforgery");
        services.AddHostedService<UploadCleanupWorker>();
        return services;
    }

    /// <summary>All routes require a stable NameIdentifier claim and antiforgery on mutations.</summary>
    public static RouteGroupBuilder MapDnaXUploads(this IEndpointRouteBuilder endpoints, string prefix = "/uploads")
    {
        var group = endpoints.MapGroup(prefix).RequireAuthorization();
        group.MapGet("/capabilities", (HttpContext context, DiskUploadStore store, IAntiforgery antiforgery) => Run(context, null, () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            string owner = Owner(context);
            return Task.FromResult<IResult>(Results.Json(new UploadCapabilities(new(store.Profiles), antiforgery.GetAndStoreTokens(context).RequestToken!,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))), UploadJsonContext.Default.UploadCapabilities));
        }));
        group.MapPost("/sessions", (HttpContext c, DiskUploadStore s, IAntiforgery a) => Run(c, a, async () =>
        {
            Limit(c, 8192);
            var request = await JsonSerializer.DeserializeAsync(c.Request.Body, UploadJsonContext.Default.UploadRequest, c.RequestAborted)
                ?? throw new UploadException(400, "Missing upload metadata.");
            return Status(await s.CreateAsync(Owner(c), request, c.RequestAborted));
        }));
        group.MapGet("/sessions/{id:guid}", (HttpContext c, Guid id, DiskUploadStore s) => Run(c, null,
            () => Task.FromResult(Status(s.Get(Owner(c), id)))));
        group.MapPut("/sessions/{id:guid}/bytes", (HttpContext c, Guid id, DiskUploadStore s, IAntiforgery a) => Run(c, a, async () =>
        {
            var status = s.Get(Owner(c), id);
            Limit(c, status.Features.Chunking ? status.Features.ChunkBytes : status.Length);
            if (c.Request.ContentType != "application/octet-stream") throw new UploadException(415, "Use application/octet-stream.");
            if (!long.TryParse(c.Request.Headers["Upload-Offset"], out long offset) || c.Request.ContentLength is not long length)
                throw new UploadException(400, "Content-Length and Upload-Offset are required.");
            return Status(await s.WriteAsync(Owner(c), id, offset, length, c.Request.Body,
                c.Request.Headers["Upload-SHA256"].FirstOrDefault(), c.RequestAborted));
        }));
        group.MapPost("/sessions/{id:guid}/verify", (HttpContext c, Guid id, DiskUploadStore s, IAntiforgery a) => Run(c, a, async () =>
        {
            Limit(c, 8192);
            var request = await JsonSerializer.DeserializeAsync(c.Request.Body, UploadJsonContext.Default.UploadVerification, c.RequestAborted)
                ?? throw new UploadException(400, "Missing verification metadata.");
            await s.VerifyAsync(Owner(c), id, request, c.RequestAborted); return Results.NoContent();
        }));
        group.MapPost("/sessions/{id:guid}/complete", (HttpContext c, Guid id, DiskUploadStore s, IAntiforgery a) => Run(c, a,
            async () => Status(await s.CompleteAsync(Owner(c), id, c.RequestAborted))));
        group.MapPost("/sessions/{id:guid}/restart", (HttpContext c, Guid id, DiskUploadStore s, IAntiforgery a) => Run(c, a,
            async () => Status(await s.RestartAsync(Owner(c), id, c.RequestAborted))));
        group.MapDelete("/sessions/{id:guid}", (HttpContext c, Guid id, DiskUploadStore s, IAntiforgery a) => Run(c, a, async () =>
        {
            await s.CancelAsync(Owner(c), id, c.RequestAborted); return Results.NoContent();
        }));
        return group;
    }

    private static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UploadException(401, "A stable upload owner is required.");
    private static IResult Status(UploadStatus status) => Results.Json(status, UploadJsonContext.Default.UploadStatus);
    private static void Limit(HttpContext c, long bytes)
    {
        var feature = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = bytes;
        if (c.Request.ContentLength > bytes) throw new UploadException(413, "Request exceeds the configured limit.");
    }
    private static async Task<IResult> Run(HttpContext c, IAntiforgery? antiforgery, Func<Task<IResult>> run)
    {
        c.Response.Headers.CacheControl = "no-store";
        try
        {
            _ = Owner(c);
            if (antiforgery is not null) await antiforgery.ValidateRequestAsync(c);
            return await run();
        }
        catch (UploadException e) { return Results.Json(new UploadError(e.Message), UploadJsonContext.Default.UploadError, statusCode: e.StatusCode); }
        catch (AntiforgeryValidationException) { return Results.Json(new UploadError("Refresh upload authorization."), UploadJsonContext.Default.UploadError, statusCode: 400); }
        catch (JsonException) { return Results.BadRequest(); }
    }

    private sealed class UploadCleanupWorker(DiskUploadStore store) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do { await store.CleanupAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}
