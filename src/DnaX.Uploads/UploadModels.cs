using System.Text.Json.Serialization;

namespace DnaX.Uploads;

/// <summary>Server-owned, immutable policy captured when a session is created.</summary>
public sealed record UploadProfile
{
    public bool Multiple { get; init; } = true;
    public bool Chunking { get; init; } = true;
    public bool Resume { get; init; } = true;
    public bool AutomaticRetry { get; init; } = true;
    public bool PersistMetadata { get; init; } = true;
    public bool ShowPause { get; init; } = true;
    public bool ShowCancel { get; init; } = true;
    public bool DragAndDrop { get; init; } = true;
    public int ChunkBytes { get; init; } = 4 * 1024 * 1024;
    public int ConcurrentFiles { get; init; } = 2;
    public int MaximumFiles { get; init; } = 20;
    public int RetryLimit { get; init; } = 4;
    public long MaximumFileBytes { get; init; } = 200L * 1024 * 1024 * 1024;
    public long PersistFileBytes { get; init; } = 16 * 1024 * 1024;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromDays(7);

    public void Validate()
    {
        if (ChunkBytes is < 65536 or > 64 * 1024 * 1024 || ConcurrentFiles is < 1 or > 16 ||
            MaximumFiles is < 1 or > 1000 || RetryLimit is < 0 or > 10 ||
            MaximumFileBytes is < 1 or > 9_007_199_254_740_991 || PersistFileBytes < 0 ||
            Lifetime <= TimeSpan.Zero || Lifetime > TimeSpan.FromDays(30) ||
            (!PersistMetadata && PersistFileBytes != 0))
            throw new ArgumentException("Invalid upload profile limits or persistence configuration.");
    }
}

public sealed class UploadOptions
{
    public string Root { get; set; } = "upload-data";
    public long MaximumReservedBytes { get; set; } = 500L * 1024 * 1024 * 1024;
    public int MaximumSessions { get; set; } = 256;
    public Dictionary<string, UploadProfile> Profiles { get; } = new(StringComparer.Ordinal);
}

public sealed record UploadRequest(Guid Id, string Profile, string FileName, long Length);
public sealed record UploadStatus(Guid Id, string Profile, string FileName, long Length, long Offset,
    string State, DateTimeOffset ExpiresAt, UploadProfile Features);
public sealed record UploadVerification(long Offset, int Length, string Sha256);
public sealed record UploadSession(string Owner, UploadStatus Status);
public sealed record UploadCapabilities(Dictionary<string, UploadProfile> Profiles, string Token, string OwnerScope);
public sealed record UploadError(string Error);

public sealed class UploadException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

[JsonSerializable(typeof(UploadRequest))]
[JsonSerializable(typeof(UploadStatus))]
[JsonSerializable(typeof(UploadSession))]
[JsonSerializable(typeof(UploadVerification))]
[JsonSerializable(typeof(UploadCapabilities))]
[JsonSerializable(typeof(UploadError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UploadJsonContext : JsonSerializerContext;
