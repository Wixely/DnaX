# Resilient uploads prototype

Status: experimental, not packable or production-ready. Reviewed 2026-09-17.

The prototype provides a .NET 10 Razor Class Library, an authenticated HTTP endpoint group, a single-process disk store, and a browser-owned multi-file queue. Once the native input is mounted, capturing files, uploading, retrying, persisting progress and displaying results do not invoke .NET or depend on the Blazor circuit.

## Run and debug

```powershell
dotnet run --project samples/DnaX.Sample.Uploads/DnaX.Sample.Uploads.csproj
dotnet test tests/DnaX.Uploads.Tests/DnaX.Uploads.Tests.csproj -c Release
```

Open `http://127.0.0.1:5189/`, or use the repository's **DNAX upload prototype** VS Code debug configuration. The launch profile enables Development static assets. The sample is a local interactive test harness, not a service deployment: it issues a protected demonstration identity cookie to each browser. Do not expose that automatic-login sample to a network. Real applications supply their existing authenticated identity. Windows build and local Chromium were verified; Linux, service deployment and Docker were not tested for this new sample.

Data lives under the sample's ignored `upload-data` directory. The store keeps files and receipts until the session's absolute expiry (seven days by default), with cleanup every ten minutes. Dismissing a browser row does not delete an accepted server file. Stop the sample before deliberately clearing its test data. Preserve the authentication/data-protection keys if you expect the same browser identity to recover uploads across server restarts.

## Integration sketch

Reference `src/DnaX.Uploads/DnaX.Uploads.csproj`; this prototype is intentionally not published as NuGet packages.

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// Configure your application's authentication and authorization separately.
builder.Services.AddDnaXUploads(options =>
{
    options.Root = Path.Combine(builder.Environment.ContentRootPath, "upload-data");
    options.MaximumReservedBytes = 500L * 1024 * 1024 * 1024;
    options.Profiles["media"] = new UploadProfile
    {
        Multiple = true,
        Chunking = true,
        Resume = true,
        AutomaticRetry = true,
        PersistMetadata = true,
        PersistFileBytes = 16 * 1024 * 1024,
        ConcurrentFiles = 2,
        MaximumFiles = 20,
        MaximumFileBytes = 200L * 1024 * 1024 * 1024
    };
});

// After Build: use your existing authentication/authorization middleware,
// followed by antiforgery, then map static assets and the upload endpoints.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDnaXUploads();
```

```razor
@using DnaX.Uploads
<UploadPicker Id="record-images" Profile="media" Endpoint="/uploads"
              @rendermode="InteractiveServer" />
```

Use a stable unique component ID, and a stable `ClaimTypes.NameIdentifier` that includes the application's tenant/domain boundary where needed. Add application-specific authorization to the returned route group. Do not trust a client-posted record ID as authorization. This prototype has no arbitrary purpose/target metadata field; application-level media/record attachment is still an integration task.

The component mounts a native browser queue on its first interactive render. It does not rerender that queue on progress or await interop notifications. The module exposes `canReloadSafely()` for an application's explicit reconnect/reload coordination. It does not override the host's reconnect behavior or prevent user/OS reloads. A host that automatically reloads must consult the barrier; the library cannot force the OS to deliver a pending picker result. The initial mount still needs the first circuit connection; selecting files subsequently does not.

The browser dispatches `dnax-upload-complete` DOM events carrying the server receipt. Events are hints: use authenticated `GET /uploads/sessions/{id}` as the authoritative reconciliation path, including after a lost notification. `DiskUploadStore.ReadCompletedAsync` streams accepted bytes into an application operation under the session gate. The application must validate content and deduplicate its own business commit by upload ID. Do not hold that gate for arbitrary long-running external processing; stage an application-owned durable job instead.

## Feature controls

Profiles are immutable records, copied at store construction and persisted with each new session. Current session mode/limits do not change when new profile configuration is deployed. Server byte limits are enforced independently of client settings. `MaximumFiles` is a browser queue limit; `MaximumSessions` and `MaximumReservedBytes` are global store admission limits, including completed/cancelled receipts until expiry. Per-owner admission quotas and durable batch manifests are future work.

| Option | Implemented behavior |
| --- | --- |
| `Multiple` | Multiple files per native selection; later selections join the queue |
| `Chunking` / `ChunkBytes` | Bounded sequential chunks per file, or one whole-file request |
| `Resume` | Retain/use incomplete progress on retry; when disabled automatic retries restart incomplete bytes |
| `AutomaticRetry` / `RetryLimit` | Bounded exponential retry after transport/conflict/server failures; independent of resume |
| `PersistMetadata` | Store browser job metadata/receipts in owner-scoped IndexedDB records |
| `PersistFileBytes` | Persist only files at or below this size; set zero with metadata persistence disabled |
| `ConcurrentFiles` | Bound active upload and hashing jobs; no unbounded batch fan-out |
| `ShowPause` / `ShowCancel` | Expose or hide queue and per-file controls |
| `DragAndDrop` | Enable or disable native drop capture |
| `MaximumFileBytes` / `Lifetime` | Server-side file limit and absolute expiry |

The minimal sample profile disables multiple selection, chunking, resume, automatic retries, browser persistence and drag/drop. These controls do not disable authorization, antiforgery, duplicate protection or byte-length validation. A custom headless UI API and fully configurable retry policies remain future work.

Whole-file mode sends the File/Blob directly in one `application/octet-stream` request and streams the request body to disk without `IFormFile` buffering. An interrupted uncommitted file retries from byte zero. Already-completed files reconcile by receipt without retransmission. Request size is set before reading the body; operators must configure upstream proxy limits and timeouts separately. No setting silently switches whole-file mode to chunking.

Chunked mode sends bounded Blob slices with offset and SHA-256 headers. The server's committed offset is authoritative. If a response is lost, the next session lookup returns the accepted offset, preventing duplicate append. After a reload/reselection the client hashes and verifies **all previously accepted bytes** in bounded ranges before resuming. This may reread a large accepted prefix on both sides but does not retransmit that prefix. Name, size and timestamp alone never authorize combining files.

## Protocol and storage boundary

The experimental routes are `GET /capabilities`, `POST /sessions`, `GET /sessions/{id}`, `PUT /sessions/{id}/bytes`, `POST /sessions/{id}/verify`, `POST /sessions/{id}/complete`, `POST /sessions/{id}/restart`, and `DELETE /sessions/{id}`. Creation accepts a client-generated random idempotency UUID, profile, filename and exact byte length. Filenames are display metadata; storage paths use UUIDs only. Every route requires an authenticated owner and every mutation validates an antiforgery token obtained over HTTP, independently of Blazor.

`complete` means all transport bytes were accepted durably. It does **not** mean an image was decoded, malware was scanned, or a record was created. Completion publishes an idempotent transport receipt without a large reassembly copy or a circuit callback. Empty files are supported. Full-file content integrity is verified in tests; the transport's incremental per-request hash verifies chunk headers, and whole-file requests do not require a browser-wide buffer to compute a hash.

The disk store uses one data file and a small JSON journal per session, fixed striped locks, and an exclusive process lease on the root. Bytes are flushed before the committed offset journal is atomically replaced. Interrupted requests truncate back to the prior offset. Startup retains journaled progress; the next write discards any unjournaled tail. Expiration cleanup shares the gates with writes/readers. Completed files remain in their staging location and are read via an owner-checked API, not served from webroot.

This is a single-process local-filesystem prototype. There is no distributed locking, database adapter, quota coordination across hosts, power-loss certification, or migration format guarantee. Expiry is absolute, not renewable; completed files must be ingested before expiry. Per-file JSON journals are a bounded prototype store, not a replacement for the proposed durable application database manifest. Application transaction/outbox semantics and production crash injection still need work.

## Large files

The sample permits 200 GiB per file and reserves up to 500 GiB globally. The core uses checked 64-bit lengths/offsets, constrained to JavaScript's safe integer range. Browser chunk hashing is bounded by configured chunk size; server streaming uses a pooled 64 KiB read buffer. The large-file path does not copy the full source into browser storage, memory or a .NET interop buffer.

**50/100 GiB end-to-end transfers have not been run.** The current evidence covers metadata admission at those sizes and a 32 MiB generated streaming test with 64 KiB maximum read requests and matching end-to-end hashes. These are useful prototype checks, not a large-file performance claim. Proxy buffering, device memory, filesystem capacity, transfer duration, long-lived auth and expiration policies must be measured before claiming massive-file support.

## Verification record

2026-09-17, Windows, .NET SDK 10.0.300:

- Release sample build: zero warnings/errors.
- 18 automated tests passed: lost responses, duplicate offsets, owner isolation, antiforgery HTTP flow, short/overlong/interrupted bodies, checksum mismatch, explicit restart, cancellation, quotas, expiry, exclusive process lease, persisted receipts and unjournaled-tail recovery.
- Existing MCPHub Playwright Chromium instance: five files completed across three profiles after forced circuit disconnection. One accepted-chunk response was deliberately lost. A 9 MiB + 31 byte chunked file used exactly three bodies (4 MiB, 4 MiB, remaining bytes); the whole-file profile used one body of the full size.
- Full reload: a 9 MiB file resumed from IndexedDB and a 17 MiB file required reselection. A wrong same-size file was rejected. Both resumed after the committed 4 MiB prefix; no accepted prefix was reuploaded.
- No Node.js/Python runtime or tooling was added or invoked. Browser scenarios were evaluated by the existing MCP browser capability. Native mobile picker/background behavior was not tested.

Reproducible browser expressions live in `tests/DnaX.Uploads.Tests/browser/`. Use a fresh isolated browser context for `basic.js`; for reload recovery run `reload-prepare.js`, navigate to the sample again, then run `reload-verify.js`. Evaluate each file as an async expression using the existing browser MCP tool. The disconnect test alone intentionally uses the runtime's observed `Blazor._internal.forceCloseConnection` hook; production library code does not use it.

## Protocol evaluation and next work

[tus](https://tus.io/protocols/resumable-upload) supplies offset-based resumable HTTP transfer, and [tusdotnet](https://github.com/tusdotnet/tusdotnet) is a suitable candidate for a production adapter. This prototype deliberately isolates the native capture/persistence/receipt experiment using a small custom transport; it **does not implement tus** and has not validated tus interoperability. This is a temporary prototype decision, not a final protocol choice. A production transport abstraction and tus spike remain required before finalizing the public API. Microsoft documents both native forms and JavaScript HTTP upload patterns in [ASP.NET Core uploads](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0).

Remaining work, owner Codex unless otherwise stated:

1. Pilot a real consumer with application-level authorization, validation and idempotent attachment. Stabilize store/client transport interfaces and test tus interoperability.
2. Run actual 50 GiB and 100 GiB transfers, memory/disk measurements, fault injection during journal publication, and whole-file interruption through a representative proxy. Allocate test space and duration first.
3. Verify physical Android/iOS file picker, suspension, reauthentication and circuit replacement; user provides device access.
4. Add renewable leases, bounded aggregate browser persistence, per-owner quotas, browser-record expiry/logout cleanup, and a supported durable business-completion contract.
5. Validate Linux and packaging/deployment requirements before publication. No consuming applications, commits, pushes or deployments were changed by this prototype.

Recommended next action: review the local sample and select the first consumer pilot; then perform device and large-transfer acceptance before release.
