namespace DnaX.Diagnostics;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record DnaXLiveResponse(string Status);

public sealed record DnaXHealthResponse(
    string Status,
    TimeSpan TotalDuration,
    IReadOnlyList<DnaXHealthEntry> Entries);

public sealed record DnaXHealthEntry(string Name, string Status, TimeSpan Duration, string? Description);

public sealed record DnaXRuntimeInfo(
    string Application,
    string? Version,
    string Framework,
    string OperatingSystem,
    string Environment,
    int ProcessorCount,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    TimeSpan ProcessUptime,
    DnaXThreadPoolInfo ThreadPool,
    IReadOnlyList<string>? Routes);

public sealed record DnaXThreadPoolInfo(
    int AvailableWorkers,
    int AvailableIo,
    int MaximumWorkers,
    int MaximumIo,
    int MinimumWorkers,
    int MinimumIo);

[JsonSerializable(typeof(DnaXHealthResponse))]
internal sealed partial class DnaXDiagnosticsJsonContext : JsonSerializerContext
{
}

// Web defaults (camelCase) match what TypedResults.Ok produced through ASP.NET Core's
// JsonOptions, so these payloads keep their wire format while no longer depending on
// the host's resolver.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DnaXLiveResponse))]
[JsonSerializable(typeof(DnaXRuntimeInfo))]
internal sealed partial class DnaXDiagnosticsWebJsonContext : JsonSerializerContext
{
}
