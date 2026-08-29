using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnaX.RemoteAccess;

internal sealed class DnaXRemoteAccessMiddleware(
    RequestDelegate next,
    IDnaXRemoteAccessRuntime runtime,
    DnaXRemoteRequestLimiter limiter,
    IOptions<DnaXRemoteAccessOptions> options,
    TimeProvider clock,
    ILogger<DnaXRemoteAccessMiddleware> logger)
{
    private static readonly object RewrittenMarker = new();
    private readonly DnaXRemoteAccessOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/_dnax/remote"))
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        DnaXRemoteEffectiveSurface api = await runtime.GetSurfaceAsync(
            DnaXRemoteSurface.Api,
            context.RequestAborted).ConfigureAwait(false);
        DnaXRemoteEffectiveSurface mcp = await runtime.GetSurfaceAsync(
            DnaXRemoteSurface.Mcp,
            context.RequestAborted).ConfigureAwait(false);
        DnaXRemoteEffectiveSurface? effective = Matches(context.Request.Path, api)
            ? api
            : Matches(context.Request.Path, mcp) ? mcp : null;
        if (effective is null)
        {
            if (IsReservedRemotePath(context.Request.Path))
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (!effective.IsAvailable || _options.Network.RequireHttps && !context.Request.IsHttps)
        {
            await WriteAndAuditAsync(context, effective, StatusCodes.Status404NotFound, DnaXRemoteAuditResult.NotFound)
                .ConfigureAwait(false);
            return;
        }

        DnaXRemoteAuthentication authentication = await runtime.AuthenticateAsync(
            effective,
            context.Request.Headers.Authorization,
            context.RequestAborted).ConfigureAwait(false);
        if (!authentication.Succeeded)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteAndAuditAsync(context, effective, StatusCodes.Status401Unauthorized, DnaXRemoteAuditResult.Unauthorized)
                .ConfigureAwait(false);
            return;
        }

        IHttpMaxRequestBodySizeFeature? bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = _options.Limits.MaximumRequestBodyBytes;
        }

        if (context.Request.ContentLength > _options.Limits.MaximumRequestBodyBytes)
        {
            await WriteAndAuditAsync(context, effective, StatusCodes.Status413PayloadTooLarge, DnaXRemoteAuditResult.Rejected)
                .ConfigureAwait(false);
            return;
        }

        string identity = authentication.ServiceIdentity ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await using DnaXRemoteRequestLease? lease = await limiter.TryAcquireAsync(
            effective.Surface,
            identity,
            context.RequestAborted).ConfigureAwait(false);
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "60";
            await WriteAndAuditAsync(context, effective, StatusCodes.Status429TooManyRequests, DnaXRemoteAuditResult.Rejected)
                .ConfigureAwait(false);
            return;
        }

        ClaimsIdentity remoteIdentity = new(
            authentication.Scopes.Select(scope => new Claim(DnaXRemoteClaimTypes.Scope, scope))
                .Append(new Claim(DnaXRemoteClaimTypes.Surface, effective.Surface.ToString()))
                .Concat(authentication.CredentialSuffix is null
                    ? []
                    : [new Claim(DnaXRemoteClaimTypes.CredentialSuffix, authentication.CredentialSuffix)]),
            authenticationType: "DnaXRemoteAccess",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);
        context.User.AddIdentity(remoteIdentity);

        PathString originalPath = context.Request.Path;
        CancellationToken originalAborted = context.RequestAborted;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(originalAborted);
        timeout.CancelAfter(_options.Limits.RequestTimeout);
        context.RequestAborted = timeout.Token;
        Rewrite(context, effective);
        int statusCode = StatusCodes.Status500InternalServerError;
        DnaXRemoteAuditResult result = DnaXRemoteAuditResult.Failed;
        try
        {
            await next(context).ConfigureAwait(false);
            statusCode = context.Response.StatusCode;
            result = statusCode < 400 ? DnaXRemoteAuditResult.Allowed : DnaXRemoteAuditResult.Failed;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !originalAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            }

            statusCode = StatusCodes.Status504GatewayTimeout;
            result = DnaXRemoteAuditResult.Rejected;
        }
        catch
        {
            statusCode = StatusCodes.Status500InternalServerError;
            result = DnaXRemoteAuditResult.Failed;
            throw;
        }
        finally
        {
            context.Request.Path = originalPath;
            context.RequestAborted = originalAborted;
            await AuditSafelyAsync(context, effective, authentication, statusCode, result).ConfigureAwait(false);
        }
    }

    private static bool Matches(PathString requestPath, DnaXRemoteEffectiveSurface effective) =>
        effective.EndpointPath is not null && requestPath.StartsWithSegments(effective.EndpointPath);

    private bool IsReservedRemotePath(PathString requestPath)
    {
        string path = requestPath.Value ?? string.Empty;
        return requestPath.StartsWithSegments(_options.Api.FixedEndpointPath) ||
               requestPath.StartsWithSegments(_options.Mcp.FixedEndpointPath) ||
               path.StartsWith("/dnax-api-", StringComparison.Ordinal) ||
               path.StartsWith("/dnax-mcp-", StringComparison.Ordinal);
    }

    private static void Rewrite(HttpContext context, DnaXRemoteEffectiveSurface effective)
    {
        PathString external = new(effective.EndpointPath!);
        context.Request.Path.StartsWithSegments(external, out PathString remainder);
        context.Items[RewrittenMarker] = true;
        context.Request.Path = effective.Surface == DnaXRemoteSurface.Api
            ? new PathString(DnaXRemoteAccessExtensions.InternalApiPrefix).Add(remainder)
            : new PathString(DnaXRemoteAccessExtensions.InternalMcpPath).Add(remainder);
    }

    private async Task WriteAndAuditAsync(
        HttpContext context,
        DnaXRemoteEffectiveSurface effective,
        int statusCode,
        DnaXRemoteAuditResult result)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        string payload = statusCode == StatusCodes.Status401Unauthorized
            ? "{\"title\":\"Unauthorized\",\"status\":401}"
            : statusCode == StatusCodes.Status404NotFound
                ? "{\"title\":\"Not found\",\"status\":404}"
                : statusCode == StatusCodes.Status413PayloadTooLarge
                    ? "{\"title\":\"Request too large\",\"status\":413}"
                    : "{\"title\":\"Request rejected\",\"status\":429}";
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        await AuditSafelyAsync(context, effective, null, statusCode, result).ConfigureAwait(false);
    }

    private async Task AuditSafelyAsync(
        HttpContext context,
        DnaXRemoteEffectiveSurface effective,
        DnaXRemoteAuthentication? authentication,
        int statusCode,
        DnaXRemoteAuditResult result)
    {
        try
        {
            string action = context.GetEndpoint()?.Metadata.GetMetadata<DnaXRemoteActionAttribute>()?.Action
                ?? $"{effective.Surface.ToString().ToLowerInvariant()}.request";
            DnaXRemoteAuditEvent auditEvent = new(
                effective.Surface,
                authentication?.ServiceIdentity,
                authentication?.CredentialSuffix,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Method,
                action,
                result,
                statusCode,
                context.TraceIdentifier,
                clock.GetUtcNow());
            await runtime.RecordAuditAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not record a redacted {Surface} remote-access audit event.", effective.Surface);
        }
    }

    private static Task NotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
