using System.Security.Cryptography;
using DnaX.Uploads;

namespace DnaX.Uploads.Tests;

public sealed class UploadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dnax-upload-test-" + Guid.NewGuid().ToString("N"));
    private UploadOptions Options() => new()
    {
        Root = root,
        Profiles = { ["chunks"] = new UploadProfile { ChunkBytes = 65536 }, ["whole"] = new UploadProfile { Chunking = false } }
    };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static UploadRequest Request(string profile = "chunks", long length = 6) => new(Guid.NewGuid(), profile, "test.bin", length);
    private static Task<UploadStatus> Write(DiskUploadStore store, Guid id, long offset, byte[] bytes) =>
        store.WriteAsync("owner", id, offset, bytes.Length, new MemoryStream(bytes), Hash(bytes));

    [Fact]
    public async Task Lost_response_is_reconciled_and_completed_receipt_survives_restart()
    {
        var request = Request();
        using (var store = new DiskUploadStore(Options()))
        {
            await store.CreateAsync("owner", request);
            await Write(store, request.Id, 0, [1, 2, 3]);
            var duplicate = await Assert.ThrowsAsync<UploadException>(() => Write(store, request.Id, 0, [1, 2, 3]));
            Assert.Equal(409, duplicate.StatusCode);
        }
        using (var store = new DiskUploadStore(Options()))
        {
            Assert.Equal(3, store.Get("owner", request.Id).Offset);
            await Write(store, request.Id, 3, [4, 5, 6]);
            var receipt = await store.CompleteAsync("owner", request.Id);
            Assert.Equal(receipt, await store.CompleteAsync("owner", request.Id));
            await store.ReadCompletedAsync("owner", request.Id, async (stream, ct) =>
            {
                var copy = new MemoryStream(); await stream.CopyToAsync(copy, ct);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, copy.ToArray());
            });
        }
        using var restored = new DiskUploadStore(Options());
        Assert.Equal("complete", (await restored.CreateAsync("owner", request)).State);
    }

    [Theory]
    [InlineData("chunks")]
    [InlineData("whole")]
    public async Task Interrupted_body_rolls_back_and_retries(string profile)
    {
        using var store = new DiskUploadStore(Options());
        var request = Request(profile); await store.CreateAsync("owner", request);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync("owner", request.Id, 0, 6,
            new InterruptedStream(), profile == "chunks" ? Hash([1, 2, 3, 4, 5, 6]) : null));
        Assert.Equal(0, store.Get("owner", request.Id).Offset);
        Assert.Equal(0, new FileInfo(Path.Combine(root, request.Id.ToString("N") + ".data")).Length);
        await Write(store, request.Id, 0, [1, 2, 3, 4, 5, 6]);
        Assert.Equal("complete", (await store.CompleteAsync("owner", request.Id)).State);
    }

    [Fact]
    public async Task Bad_hash_and_overlong_body_never_advance_committed_offset()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); await store.CreateAsync("owner", request);
        await Write(store, request.Id, 0, [1, 2, 3]);
        Assert.Equal(422, (await Assert.ThrowsAsync<UploadException>(() => store.WriteAsync("owner", request.Id, 3, 3,
            new MemoryStream([4, 5, 6]), Hash([0])))).StatusCode);
        Assert.Equal(413, (await Assert.ThrowsAsync<UploadException>(() => store.WriteAsync("owner", request.Id, 3, 2,
            new MemoryStream([4, 5, 6]), Hash([4, 5])))).StatusCode);
        Assert.Equal(3, store.Get("owner", request.Id).Offset);
        await store.VerifyAsync("owner", request.Id, new(0, 3, Hash([1, 2, 3])));
    }

    [Fact]
    public async Task Restart_truncates_unjournaled_tail_before_new_write()
    {
        var request = Request();
        using (var store = new DiskUploadStore(Options())) { await store.CreateAsync("owner", request); await Write(store, request.Id, 0, [1, 2, 3]); }
        await using (var file = new FileStream(Path.Combine(root, request.Id.ToString("N") + ".data"), FileMode.Append))
            await file.WriteAsync(new byte[] { 99, 99 });
        using var restored = new DiskUploadStore(Options()); await Write(restored, request.Id, 3, [4, 5, 6]);
        await restored.VerifyAsync("owner", request.Id, new(0, 6, Hash([1, 2, 3, 4, 5, 6])));
    }

    [Fact]
    public async Task Resume_reselection_checks_previously_accepted_bytes()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); await store.CreateAsync("owner", request);
        await Write(store, request.Id, 0, [1, 2, 3]);
        await store.VerifyAsync("owner", request.Id, new(0, 3, Hash([1, 2, 3])));
        Assert.Equal(422, (await Assert.ThrowsAsync<UploadException>(() => store.VerifyAsync("owner", request.Id,
            new(0, 3, Hash([9, 2, 3]))))).StatusCode);
    }

    [Fact]
    public async Task Profiles_are_pinned_and_whole_file_mode_rejects_partial_requests()
    {
        var options = Options(); using var store = new DiskUploadStore(options);
        var request = Request("whole"); var status = await store.CreateAsync("owner", request);
        options.Profiles["whole"] = new UploadProfile();
        Assert.False(store.Get("owner", request.Id).Features.Chunking);
        Assert.Equal(413, (await Assert.ThrowsAsync<UploadException>(() => Write(store, request.Id, 0, [1, 2, 3]))).StatusCode);
        await Write(store, request.Id, 0, [1, 2, 3, 4, 5, 6]);
        Assert.Equal(6, store.Get("owner", request.Id).Offset);
    }

    [Fact]
    public async Task Owner_and_idempotency_key_are_enforced()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); var status = await store.CreateAsync("owner", request);
        Assert.Equal(status, await store.CreateAsync("owner", request));
        Assert.Equal(404, Assert.Throws<UploadException>(() => store.Get("other", request.Id)).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<UploadException>(() => store.CancelAsync("other", request.Id))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<UploadException>(() => store.CreateAsync("owner", request with { Length = 7 }))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<UploadException>(() => store.CompleteAsync("owner", request.Id))).StatusCode);
    }

    [Fact]
    public async Task Parallel_duplicate_writers_cannot_append_twice()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); await store.CreateAsync("owner", request);
        var writes = Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await Write(store, request.Id, 0, [1, 2, 3]); return true; }
            catch (UploadException ex) when (ex.StatusCode == 409) { return false; }
        });
        Assert.Single(await Task.WhenAll(writes), success => success); Assert.Equal(3, store.Get("owner", request.Id).Offset);
    }

    [Fact]
    public async Task Cancelling_one_file_does_not_cancel_others()
    {
        using var store = new DiskUploadStore(Options()); var a = Request(); var b = Request();
        await store.CreateAsync("owner", a); await store.CreateAsync("owner", b);
        await store.CancelAsync("owner", a.Id); await Write(store, b.Id, 0, [1, 2, 3, 4, 5, 6]);
        Assert.Equal("complete", (await store.CompleteAsync("owner", b.Id)).State);
        Assert.Equal(409, (await Assert.ThrowsAsync<UploadException>(() => Write(store, a.Id, 0, [1]))).StatusCode);
    }

    [Fact]
    public async Task Explicit_restart_discards_partial_progress_but_preserves_complete_receipt()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); await store.CreateAsync("owner", request);
        await Write(store, request.Id, 0, [1, 2, 3]);
        Assert.Equal(0, (await store.RestartAsync("owner", request.Id)).Offset);
        await Write(store, request.Id, 0, [4, 5, 6, 7, 8, 9]);
        var receipt = await store.CompleteAsync("owner", request.Id);
        Assert.Equal(receipt, await store.RestartAsync("owner", request.Id));
    }

    [Fact]
    public async Task Whole_file_streaming_uses_bounded_reads()
    {
        using var store = new DiskUploadStore(Options());
        const int length = 32 * 1024 * 1024;
        var request = Request("whole", length); await store.CreateAsync("owner", request);
        using var source = new GeneratedStream(length);
        await store.WriteAsync("owner", request.Id, 0, length, source);
        Assert.True(source.LargestRead <= 65536);
        await store.CompleteAsync("owner", request.Id);
        await store.ReadCompletedAsync("owner", request.Id, async (stream, ct) =>
        {
            using var expected = new GeneratedStream(length);
            Assert.Equal(await SHA256.HashDataAsync(expected, ct), await SHA256.HashDataAsync(stream, ct));
        });
    }

    [Theory]
    [InlineData(53_687_091_200L)]
    [InlineData(107_374_182_400L)]
    public async Task Massive_lengths_are_reserved_without_allocating_file_bytes(long length)
    {
        using var store = new DiskUploadStore(Options()); var request = Request(length: length);
        Assert.Equal(length, (await store.CreateAsync("owner", request)).Length);
        Assert.False(File.Exists(Path.Combine(root, request.Id.ToString("N") + ".data")));
        // Metadata-only coverage. This is deliberately not a real 50/100 GiB transfer claim.
    }

    [Fact]
    public async Task Reservations_and_expiry_are_enforced_and_cleanup_releases_capacity()
    {
        var clock = new TestTime(); var options = Options(); options.MaximumReservedBytes = 6;
        using var store = new DiskUploadStore(options, clock); var request = Request(); await store.CreateAsync("owner", request);
        Assert.Equal(429, (await Assert.ThrowsAsync<UploadException>(() => store.CreateAsync("owner", Request()))).StatusCode);
        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(410, Assert.Throws<UploadException>(() => store.Get("owner", request.Id)).StatusCode);
        await store.CleanupAsync(); await store.CreateAsync("owner", Request());
    }

    [Fact]
    public void Root_has_exclusive_process_lease()
    {
        using var store = new DiskUploadStore(Options()); Assert.Throws<IOException>(() => new DiskUploadStore(Options()));
    }

    [Fact]
    public async Task Cancellation_token_rolls_back_partial_chunk()
    {
        using var store = new DiskUploadStore(Options()); var request = Request(); await store.CreateAsync("owner", request);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("owner", request.Id, 0, 6,
            new MemoryStream([1, 2, 3, 4, 5, 6]), Hash([1, 2, 3, 4, 5, 6]), cancellation.Token));
        Assert.Equal(0, store.Get("owner", request.Id).Offset);
    }

    private sealed class TestTime : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class InterruptedStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("Simulated disconnection."); read = true;
            buffer.Span[0] = 1; return ValueTask.FromResult(1);
        }
    }
    private sealed class GeneratedStream(long remaining) : Stream
    {
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            int size = (int)Math.Min(buffer.Length, remaining); buffer.Span[..size].Fill(73); remaining -= size; return ValueTask.FromResult(size);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
