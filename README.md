# DNA X

DNA X is a set of focused .NET 10 libraries that preserve DNA's useful keyed closure APIs while using the modern Generic Host, dependency injection, async I/O, cancellation, and secure ASP.NET Core primitives.

Packages are deliberately independent:

- `DnaX.Hosting` resolves content and writable-data paths consistently in IIS, Windows Services, console hosts, and tests.
- `DnaX.Data` executes closures against named, provider-neutral `DbConnection` factories without installing SQL Server, Oracle, Dapper, or EF Core.
- `DnaX.Data.Migrations` defines explicit, checksummed migration manifests and named migration orchestration, with opt-in adapters for SQLite, PostgreSQL, SQL Server, and Oracle.
- `DnaX.Caching` provides single-flight `Hit`/`HitAsync`, stale-while-refresh, invalidation, null policy, and testable time.
- `DnaX.Redis.StackExchangeRedis` is an opt-in StackExchange.Redis adapter with named closure execution.
- `DnaX.Diagnostics` maps health and sanitized runtime endpoints onto an existing ASP.NET Core host.
- `DnaX.RemoteAccess` adds deliberately enabled, deployment-bound API access with rotatable credentials and endpoints, limits, safe diagnostics, and redacted auditing.
- `DnaX.RemoteAccess.Sqlite` persists remote-access state and bounded audit events using an explicit `DnaX.Data.Migrations` manifest.
- `DnaX.RemoteAccess.Mcp` hosts application-provided tools over authenticated, stateless Streamable HTTP using the official MCP SDK.
- `DnaX.Compatibility` contains only golden-tested legacy contracts.

See [examples.md](examples.md) for worked examples and
[`DnaX.Sample.Web`](samples/DnaX.Sample.Web/Program.cs) for composed hosting.

## Host-independent files

```csharp
builder.Services.AddDnaXHosting(options =>
    options.WritableDataRoot = "state");

// The same call works under IIS, Windows Service, console, and test hosts.
string sql = await paths.ReadAllTextAsync(
    "SqlScripts/report.sql",
    cancellationToken);
```

`IDnaXPaths` resolves content from the Generic Host content root, never from the process working directory. Relative paths are constrained to their configured root.

## Named SQL Server or Oracle closures

`DnaX.Data` is provider-neutral. Install only the ADO.NET provider used by the application:

```csharp
builder.Services.AddDnaXData()
    .AddDatabase("Primary", _ => new SqlConnection(primaryConnectionString))
    .AddDatabase("Reporting", _ => new OracleConnection(reportingConnectionString));

ReportRow[] rows = await databases.ExecuteAsync(
    "Reporting",
    async (connection, cancellationToken) =>
        (await connection.QueryAsync<ReportRow>(sql)).ToArray(),
    cancellationToken);
```

SQL Server, Oracle, and Dapper appear here only in application code. `DnaX.Data` does not depend on any of them.

## Database schema manifests and migrations

```csharp
builder.Services.AddDnaXDataMigrations("Primary", options =>
{
    options.ConnectionFactory = _ => new SqliteConnection(connectionString);
    options.Manifest = ApplicationSchema.Manifest;
    options.ApplicationVersion = "1.4.0";
    options.UseSqlite(); // Or UsePostgreSql(), UseSqlServer(), or UseOracle().
});

// Explicitly migrate before accepting traffic.
await app.Services.MigrateDnaXDatabaseAsync("Primary");
```

The application owns and reviews every provider-specific SQL statement. DNA X owns immutable manifest validation, the checksum ledger, provider-appropriate migration locking, structured diagnostics, and explicit transaction guarantees. Dapper remains an application dependency and can be used normally after migration.

See [database manifests and migrations](docs/database-migrations.md) for provider setup, manifest authoring, startup migration, legacy database adoption, transaction behavior, and CI verification.

## Cache closures

```csharp
Report report = await cache.HitAsync(
    "reports:daily",
    BuildReportAsync,
    new DnaXCacheEntryOptions
    {
        Duration = TimeSpan.FromMinutes(5),
        StaleDuration = TimeSpan.FromMinutes(30),
        RefreshFailureCooldown = TimeSpan.FromMinutes(1)
    },
    cancellationToken);
```

Cold calls are single-flight. During the stale window callers receive the previous value while one host-managed background refresh runs.

## Named Redis closures

Install `DnaX.Redis.StackExchangeRedis` only in applications that use Redis:

```csharp
builder.Services.AddDnaXRedis()
    .AddRedis("Session", redisConnectionString, database: 1);

RedisValue value = await redis.ExecuteAsync(
    "Session",
    async (database, _) => await database.StringGetAsync(key),
    cancellationToken);
```

The named multiplexer is created once and disposed with the host.

## Opt-in remote API and MCP access

Remote access is unavailable by default. A consuming application must register the packages and its own API operations or MCP tools, enable the deployment policy, create credentials, and activate each surface through `IDnaXRemoteAccessAdministration` before a route will accept requests.

```csharp
builder.Services.AddDnaXRemoteAccess(options =>
{
    options.Enabled = true;
    options.DeploymentId = "my-service-production";

    options.Api.Available = true;
    options.Api.AllowRuntimeActivation = true;
    options.Api.AllowCredentialRotation = true;
    options.Api.AllowEndpointRotation = true;

    options.Mcp.Available = true;
    options.Mcp.AllowRuntimeActivation = true;
    options.Mcp.AllowCredentialRotation = true;
    options.Mcp.AllowEndpointRotation = true;
});

builder.Services.AddDnaXRemoteAccessSqlite(
    "RemoteAccess",
    _ => new SqliteConnection(remoteAccessConnectionString));

builder.Services.AddDnaXRemoteMcp()
    .WithTools<ApplicationMcpTools>();

WebApplication app = builder.Build();
await app.Services.MigrateDnaXDatabaseAsync("RemoteAccess");

app.UseDnaXRemoteAccess();
app.UseRouting();

app.MapDnaXRemoteApi()
    .MapGet(
        "/status",
        async (ApplicationOperations operations, CancellationToken cancellationToken) =>
            await operations.GetStatusAsync(cancellationToken))
    .WithDnaXRemoteAction("application.get_status");

app.MapDnaXRemoteMcp();
```

API and MCP policy is independent. Randomized endpoints are the default, but remain defense in depth rather than authentication. API credentials are always required; anonymous MCP additionally requires explicit deployment and administrator opt-in. Tokens are returned only when created or rotated, while SQLite stores a versioned hash, identifying suffix, lifecycle metadata, protected route state, and metadata-only audit events. A deployment identifier prevents restored state from silently activating on another deployment.

Applications continue to own DTOs, operations, scopes, authorization policy, tool definitions, confirmation rules, and risk-specific validation. The packages do not expose databases, configuration, files, SQL, or shell access automatically.

## Diagnostics

```csharp
builder.Services.AddDnaXDiagnostics(options =>
{
    options.EnableDetails = true;
    options.AuthorizationPolicy = "Operations";
});

app.MapDnaXDiagnostics();
```

The `/_dna/live` and `/_dna/ready` endpoints are mapped onto the existing ASP.NET Core host. Authorization is required by default; detailed runtime information and route listing are explicit opt-ins.

## License

DNA X is licensed under the [MIT License](LICENSE). Optional dependencies retain their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
