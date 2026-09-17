using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DnaX.Uploads;

/// <summary>Experimental single-process store. Holds an exclusive root lease; committed offsets survive restart.</summary>
public sealed class DiskUploadStore : IDisposable
{
    private readonly UploadOptions options;
    private readonly TimeProvider time;
    private readonly FileStream lease;
    private readonly ConcurrentDictionary<Guid, UploadSession> sessions = new();
    private readonly SemaphoreSlim creation = new(1);
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public IReadOnlyDictionary<string, UploadProfile> Profiles { get; }

    public DiskUploadStore(UploadOptions configuration, TimeProvider? time = null)
    {
        var options = new UploadOptions { Root = configuration.Root, MaximumReservedBytes = configuration.MaximumReservedBytes,
            MaximumSessions = configuration.MaximumSessions };
        foreach (var pair in configuration.Profiles) options.Profiles.Add(pair.Key, pair.Value);
        this.options = options;
        this.time = time ?? TimeProvider.System;
        if (options.MaximumSessions < 1 || options.MaximumReservedBytes < 1 || options.Profiles.Count == 0)
            throw new ArgumentException("Configure upload profiles and positive storage limits.");
        foreach (var profile in options.Profiles.Values) profile.Validate();
        Profiles = new Dictionary<string, UploadProfile>(options.Profiles, StringComparer.Ordinal);
        options.Root = Path.GetFullPath(options.Root);
        Directory.CreateDirectory(options.Root);
        lease = new FileStream(Path.Combine(options.Root, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var path in Directory.EnumerateFiles(options.Root, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                var session = JsonSerializer.Deserialize(File.ReadAllText(path), UploadJsonContext.Default.UploadSession)
                    ?? throw new InvalidDataException("Invalid upload journal.");
                if (session.Status.Id != id || session.Status.Offset < 0 || session.Status.Offset > session.Status.Length)
                    throw new InvalidDataException("Invalid upload journal identity or offset.");
                sessions[id] = session;
            }
        }
        catch { lease.Dispose(); throw; }
    }

    private SemaphoreSlim Gate(Guid id) => gates[(id.GetHashCode() & int.MaxValue) % gates.Length];
    private string Data(Guid id) => Path.Combine(options.Root, id.ToString("N") + ".data");
    private string Journal(Guid id) => Path.Combine(options.Root, id.ToString("N") + ".json");
    private UploadSession Owned(Guid id, string owner)
    {
        if (!sessions.TryGetValue(id, out var session) || session.Owner != owner)
            throw new UploadException(404, "Upload not found.");
        if (session.Status.ExpiresAt <= time.GetUtcNow()) throw new UploadException(410, "Upload expired.");
        return session;
    }

    private async Task CommitAsync(UploadSession session)
    {
        var target = Journal(session.Status.Id);
        var temp = target + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            await JsonSerializer.SerializeAsync(file, session, UploadJsonContext.Default.UploadSession);
            file.Flush(true);
        }
        File.Move(temp, target, true);
        sessions[session.Status.Id] = session;
    }

    public async Task<UploadStatus> CreateAsync(string owner, UploadRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Profile) || string.IsNullOrWhiteSpace(request.FileName) ||
            request.FileName.Length > 255 || request.FileName.Any(char.IsControl) || request.Length < 0)
            throw new UploadException(400, "Invalid upload metadata.");
        await creation.WaitAsync(ct);
        try
        {
            if (sessions.ContainsKey(request.Id))
            {
                var existing = Owned(request.Id, owner).Status;
                if (existing.Profile != request.Profile || existing.FileName != request.FileName || existing.Length != request.Length)
                    throw new UploadException(409, "Upload identity was already used for different metadata.");
                return existing;
            }
            if (!Profiles.TryGetValue(request.Profile, out var profile)) throw new UploadException(400, "Unknown profile.");
            if (request.Length > profile.MaximumFileBytes) throw new UploadException(413, "File exceeds the profile limit.");
            // Reservations include completed files until expiration and cleanup.
            long reserved = sessions.Values.Sum(s => s.Status.Length);
            if (sessions.Count >= options.MaximumSessions || request.Length > options.MaximumReservedBytes - reserved)
                throw new UploadException(429, "Upload storage reservation limit reached.");
            var status = new UploadStatus(request.Id, request.Profile, request.FileName, request.Length, 0,
                "receiving", time.GetUtcNow() + profile.Lifetime, profile);
            await CommitAsync(new(owner, status));
            return status;
        }
        finally { creation.Release(); }
    }

    public UploadStatus Get(string owner, Guid id) => Owned(id, owner).Status;

    public async Task<UploadStatus> RestartAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var session = Owned(id, owner);
            if (session.Status.State == "complete") return session.Status;
            if (session.Status.State != "receiving") throw new UploadException(409, "Upload cannot be restarted.");
            var status = session.Status with { Offset = 0 };
            // Journal rollback first. A crash leaves an uncommitted tail, removed on the next write.
            await CommitAsync(session with { Status = status });
            File.Delete(Data(id));
            return status;
        }
        finally { gate.Release(); }
    }

    public async Task<UploadStatus> WriteAsync(string owner, Guid id, long offset, long length,
        Stream input, string? sha256 = null, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var session = Owned(id, owner);
            var status = session.Status;
            if (status.State == "complete") return status;
            if (status.State != "receiving") throw new UploadException(409, "Upload is not receiving bytes.");
            var chunked = status.Features.Chunking;
            if (offset != (chunked ? status.Offset : 0)) throw new UploadException(409, "Offset conflict. Query upload status.");
            if (length < 0 || length > status.Length - offset ||
                (chunked && (length > status.Features.ChunkBytes || (length == 0 && status.Length != 0))) ||
                (!chunked && length != status.Length)) throw new UploadException(413, "Invalid request length.");
            if (chunked && (sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)))
                throw new UploadException(400, "Chunk SHA-256 is required.");
            long committed = chunked ? status.Offset : 0;
            await using var output = new FileStream(Data(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, true);
            if (output.Length < committed) throw new UploadException(500, "Committed upload data is unavailable.");
            output.SetLength(committed); // Roll back any unjournaled tail left by process termination.
            output.Position = committed;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read == 0) throw new UploadException(400, "Request body ended early.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new UploadException(413, "Request body is too long.");
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (sha256 is not null && !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UploadException(422, "Chunk checksum mismatch.");
                output.Flush(true);
                var next = status with { Offset = checked(offset + length) };
                // No request cancellation between durable bytes and the journal publication.
                await CommitAsync(session with { Status = next });
                return next;
            }
            catch { output.SetLength(committed); output.Flush(true); throw; }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { gate.Release(); }
    }

    public async Task VerifyAsync(string owner, Guid id, UploadVerification verification, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var status = Owned(id, owner).Status;
            if (verification.Offset < 0 || verification.Length < 1 || verification.Length > status.Features.ChunkBytes ||
                verification.Offset > status.Offset - verification.Length)
                throw new UploadException(400, "Invalid verification range.");
            await using var file = new FileStream(Data(id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            file.Position = verification.Offset;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int remaining = verification.Length;
                while (remaining > 0)
                {
                    int count = await file.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
                    if (count == 0) throw new UploadException(500, "Committed upload data is unavailable.");
                    hash.AppendData(buffer, 0, count); remaining -= count;
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(verification.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UploadException(422, "Selected file does not match the accepted bytes.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { gate.Release(); }
    }

    public async Task<UploadStatus> CompleteAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var session = Owned(id, owner);
            if (session.Status.State == "complete") return session.Status;
            if (session.Status.State != "receiving" || session.Status.Offset != session.Status.Length)
                throw new UploadException(409, "Upload has not received all bytes.");
            if (session.Status.Length == 0 && !File.Exists(Data(id)))
            {
                using var empty = File.Create(Data(id)); empty.Flush(true);
            }
            if (!File.Exists(Data(id)) || new FileInfo(Data(id)).Length != session.Status.Length)
                throw new UploadException(500, "Committed upload data is unavailable.");
            var status = session.Status with { State = "complete" };
            await CommitAsync(session with { Status = status });
            return status;
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var session = Owned(id, owner);
            if (session.Status.State == "complete") throw new UploadException(409, "Completed uploads cannot be cancelled.");
            await CommitAsync(session with { Status = session.Status with { State = "cancelled", Offset = 0 } });
            File.Delete(Data(id));
        }
        finally { gate.Release(); }
    }

    /// <summary>Reads accepted bytes under the session gate. Applications still own validation and idempotent business commits.</summary>
    public async Task ReadCompletedAsync(string owner, Guid id, Func<Stream, CancellationToken, Task> read, CancellationToken ct = default)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            if (Owned(id, owner).Status.State != "complete") throw new UploadException(409, "Upload is incomplete.");
            await using var file = new FileStream(Data(id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await read(file, ct);
        }
        finally { gate.Release(); }
    }

    public async Task CleanupAsync(CancellationToken ct = default)
    {
        await creation.WaitAsync(ct);
        try
        {
            foreach (var id in sessions.Keys)
            {
                var gate = Gate(id); await gate.WaitAsync(ct);
                try
                {
                    if (sessions.TryGetValue(id, out var session) && session.Status.ExpiresAt <= time.GetUtcNow())
                    {
                        File.Delete(Data(id)); File.Delete(Journal(id)); File.Delete(Journal(id) + ".tmp");
                        sessions.TryRemove(id, out _);
                    }
                }
                finally { gate.Release(); }
            }
        }
        finally { creation.Release(); }
    }

    public void Dispose() { lease.Dispose(); creation.Dispose(); foreach (var gate in gates) gate.Dispose(); }
}
