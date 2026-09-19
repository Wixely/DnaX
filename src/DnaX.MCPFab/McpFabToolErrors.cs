using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DnaX.MCPFab;

/// <summary>
/// Turns an exception thrown by a tool into a result the caller can act on.
/// </summary>
/// <remarks>
/// <para>
/// By default the SDK replies "An error occurred invoking '&lt;tool&gt;'." and writes the real cause
/// to the server log. That is a sound default for arbitrary servers, but these tools throw
/// deliberate guidance - "Set Redis:AllowDestructive=true to permit it", "Unknown alias 'x'.
/// Configured aliases: a, b" - and an agent that cannot see it has no way to correct itself.
/// It retries the same call, or reports a failure whose cause is only visible to whoever can
/// read the server's log file.
/// </para>
/// <para>
/// Only exceptions that represent a deliberate refusal are surfaced. Anything else is reported
/// by type alone, because an unexpected exception's message can carry connection strings, paths
/// or key material. The full detail is logged either way.
/// </para>
/// </remarks>
public static class McpFabToolErrors
{
    /// <summary>
    /// Exception types a tool throws on purpose, whose messages are written for the caller.
    /// </summary>
    private static bool IsDeliberateRefusal(Exception exception) => exception switch
    {
        McpException => true,
        InvalidOperationException => true,
        ArgumentException => true,
        NotSupportedException => true,
        KeyNotFoundException => true,
        _ => false,
    };

    /// <summary>
    /// Renders a failed tool call as the shared error envelope.
    /// </summary>
    /// <remarks>
    /// Separated from the filter so the decision - what a caller is told, and what it is not -
    /// is testable without constructing an <c>McpServer</c> and a transport.
    /// </remarks>
    internal static string DescribeFailure(string tool, Exception exception)
    {
        bool deliberate = IsDeliberateRefusal(exception);

        string message = deliberate
            ? exception.Message
            : $"'{tool}' failed with {exception.GetType().Name}. See the server log for detail.";

        return McpJson
            .Error(deliberate ? "tool_refused" : "tool_failed", message)
            .Set("tool", tool)
            .ToJsonString();
    }

    /// <summary>
    /// Builds the call-tool filter. Registered by <see cref="McpFabHost"/> for every server.
    /// </summary>
    /// <remarks>
    /// MCPEXP002 is suppressed deliberately and narrowly. The filter pipeline is marked "for
    /// evaluation purposes only and subject to change or removal", and it is the only hook that
    /// reaches every tool without editing each one - the estate has 824. Suppressing here rather
    /// than through NoWarn keeps the dependency greppable and confined to this file, and if the
    /// API is removed the failure is a compile error at upgrade time, not silent behaviour.
    /// </remarks>
#pragma warning disable MCPEXP002
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(McpFabToolErrors).FullName!);

        return next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string tool = context.Params?.Name ?? "unknown";
                logger.LogError(exception, "Tool {Tool} failed.", tool);

                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = DescribeFailure(tool, exception) }],
                };
            }
        };
    }
#pragma warning restore MCPEXP002
}
