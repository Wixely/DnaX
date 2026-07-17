namespace DnaX.Diagnostics;

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
