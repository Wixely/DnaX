using System.ComponentModel;
using ModelContextProtocol.Server;

public sealed record SampleStatus(string Status, DateTimeOffset ObservedAtUtc);

public sealed record SampleRootResponse(string Message, string ContentRoot, string WritableDataRoot);

public sealed class SampleRemoteOperation(TimeProvider clock)
{
    public ValueTask<SampleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SampleStatus("ready", clock.GetUtcNow()));
    }
}

[McpServerToolType]
public sealed class SampleRemoteTools
{
    [McpServerTool(Name = "get_status", UseStructuredContent = true)]
    [Description("Gets the same read-only sample status exposed by the HTTP API.")]
    public static ValueTask<SampleStatus> GetStatusAsync(
        SampleRemoteOperation operation,
        CancellationToken cancellationToken = default) =>
        operation.GetStatusAsync(cancellationToken);
}
