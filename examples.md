# DNA X examples

Worked examples of how each DNA X package is expected to be used. All
snippets assume a Generic Host application (`WebApplication.CreateBuilder`
or `Host.CreateApplicationBuilder`). Packages are independent — install
only what the application needs.

- [DnaX.Hosting — host-independent paths](#dnaxhosting--host-independent-paths)
- [DnaX.Data — named database closures](#dnaxdata--named-database-closures)
- [DnaX.Data.Migrations — SQLite schema lifecycle](#dnaxdata-migrations--sqlite-schema-lifecycle)
- [DnaX.Caching — single-flight cache closures](#dnaxcaching--single-flight-cache-closures)
- [DnaX.Redis.StackExchangeRedis — named Redis closures](#dnaxredisstackexchangeredis--named-redis-closures)
- [DnaX.Diagnostics — health and runtime endpoints](#dnaxdiagnostics--health-and-runtime-endpoints)
- [DnaX.Compatibility — golden legacy contracts](#dnaxcompatibility--golden-legacy-contracts)
- [Putting it together](#putting-it-together)

## DnaX.Hosting — host-independent paths

`IDnaXPaths` resolves files from the host content root, never from the
process working directory, so the same code behaves identically under
IIS, a Windows Service, a console host, and a test host.

### Registration

```csharp
builder.Services.AddDnaXHosting();

// Or configure where mutable data lives (default is "data" under the
// content root; a rooted path such as @"D:\AppState" is also accepted):
builder.Services.AddDnaXHosting(options =>
    options.WritableDataRoot = "state");
```

### Reading deployed content

```csharp
public sealed class ReportQueries(IDnaXPaths paths)
{
    public async Task<string> LoadSqlAsync(CancellationToken cancellationToken)
    {
        // Resolved against the content root. Relative paths are constrained
        // to the root — "../secrets.txt" throws rather than escaping it.
        return await paths.ReadAllTextAsync(
            "SqlScripts/daily-report.sql",
            cancellationToken);
    }
}
```

### Writing application state

```csharp
public sealed class ExportService(IDnaXPaths paths)
{
    public async Task SaveAsync(byte[] payload, CancellationToken cancellationToken)
    {
        // ResolveWritable maps into the configured WritableDataRoot and
        // returns an absolute path suitable for normal file APIs.
        string target = paths.ResolveWritable("exports/latest.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, payload, cancellationToken);
    }
}
```

### Streaming and file metadata

```csharp
if (paths.GetFileInfo("templates/invoice.html").Exists)
{
    await using Stream stream = paths.OpenRead("templates/invoice.html");
    // ...
}
```

## DnaX.Data — named database closures

`DnaX.Data` executes closures against named `DbConnection` factories. It
is provider-neutral: SQL Server, Oracle, and Dapper appear only in your
application code and package references, never in DNA X itself.

### Registration

```csharp
// The application installs Microsoft.Data.SqlClient and/or
// Oracle.ManagedDataAccess.Core itself.
builder.Services.AddDnaXData()
    .AddDatabase("Primary", () => new SqlConnection(
        builder.Configuration.GetConnectionString("Primary")))
    .AddDatabase("Reporting", serviceProvider => new OracleConnection(
        serviceProvider.GetRequiredService<IOptions<ReportingOptions>>()
            .Value.ConnectionString));
```

### Queries

The connection is opened, handed to the closure, and disposed for you.
Unknown names throw `DnaXDatabaseNotFoundException` listing what is
registered.

```csharp
public sealed class OrderRepository(IDnaXDatabases databases)
{
    public ValueTask<Order[]> GetOpenOrdersAsync(CancellationToken cancellationToken)
        => databases.ExecuteAsync(
            "Primary",
            async (connection, ct) =>
                (await connection.QueryAsync<Order>(          // Dapper, from app code
                    "SELECT * FROM Orders WHERE Status = 'Open'")).ToArray(),
            cancellationToken);
}
```

### Commands and transactions

```csharp
await databases.ExecuteInTransactionAsync(
    "Primary",
    async (connection, transaction, ct) =>
    {
        await connection.ExecuteAsync(
            "UPDATE Accounts SET Balance = Balance - @Amount WHERE Id = @From",
            new { Amount = amount, From = fromId }, transaction);
        await connection.ExecuteAsync(
            "UPDATE Accounts SET Balance = Balance + @Amount WHERE Id = @To",
            new { Amount = amount, To = toId }, transaction);
        return true;
    },
    IsolationLevel.Serializable,
    cancellationToken);
// The transaction commits when the closure completes and rolls back if it throws.
```

### Synchronous call sites

Legacy code paths that cannot be async yet can use the synchronous shape:

```csharp
int count = databases.Execute(
    "Primary",
    connection => connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Orders"));
```

## DnaX.Data.Migrations — SQLite schema lifecycle

Install `DnaX.Data.Migrations.Sqlite`; it brings the provider-neutral migration core without changing `DnaX.Data` or adding Dapper to DNA X. Declare migrations in application-owned source:

```csharp
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Microsoft.Data.Sqlite;

public static class ApplicationSchema
{
    public static DnaXMigrationManifest Manifest { get; } = new(
        currentVersion: 2,
        migrations:
        [
            DnaXMigration.Sql(1, "create-items", "Create items", """
                CREATE TABLE Items (
                    Id INTEGER NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                """),
            DnaXMigration.Sql(2, "index-item-names", "Index item names", """
                CREATE UNIQUE INDEX UX_Items_Name ON Items(Name);
                """)
        ]);
}
```

Register the named database and migrate explicitly before serving requests:

```csharp
builder.Services.AddDnaXDataMigrations("Primary", options =>
{
    options.ConnectionFactory = _ => new SqliteConnection(connectionString);
    options.Manifest = ApplicationSchema.Manifest;
    options.ApplicationVersion = "1.0.0";
    options.UseSqlite();
});

WebApplication app = builder.Build();
await app.Services.MigrateDnaXDatabaseAsync("Primary");
app.Run();
```

Set `options.MigrateOnStartup = true` for Generic Host startup migration instead. Explicit migration is usually clearer because its position before traffic is visible in `Program.cs`.

The runner validates versions, stable identifiers, names, and SHA-256 checksums; holds a SQLite write lock; applies the entire pending chain transactionally; and records a migration only when the chain commits. See [the full migration guide](docs/database-migrations.md) for embedded SQL, code callbacks, testing, diagnostics, and existing-database adoption.

## DnaX.Caching — single-flight cache closures

`IDnaXCache.Hit`/`HitAsync` wrap a factory closure: on a miss the factory
runs exactly once no matter how many callers race (single-flight), and
everyone receives the same value.

### Registration

```csharp
builder.Services.AddDnaXCaching();

// Or set process-wide defaults:
builder.Services.AddDnaXCaching(options =>
{
    options.DefaultDuration = TimeSpan.FromMinutes(5);
    options.DefaultStaleDuration = TimeSpan.FromMinutes(30);
    options.CacheNullsByDefault = true;
    options.DefaultRefreshFailureCooldown = TimeSpan.FromMinutes(1);
});
```

### Basic hit

```csharp
public sealed class PriceService(IDnaXCache cache)
{
    public ValueTask<PriceSheet> GetPricesAsync(CancellationToken cancellationToken)
        => cache.HitAsync(
            "prices:current",
            ct => LoadPricesAsync(ct),        // runs once per expiry, single-flight
            cancellationToken: cancellationToken);
}
```

### Stale-while-refresh

With a `StaleDuration`, expired entries keep serving the previous value
while one host-managed background refresh rebuilds the entry. Callers
never block on the refresh, and a failing refresh backs off for
`RefreshFailureCooldown` before retrying.

```csharp
Report report = await cache.HitAsync(
    "reports:daily",
    BuildReportAsync,
    new DnaXCacheEntryOptions
    {
        Duration = TimeSpan.FromMinutes(5),        // fresh window
        StaleDuration = TimeSpan.FromMinutes(30),  // serve-stale window after that
        RefreshFailureCooldown = TimeSpan.FromMinutes(1)
    },
    cancellationToken);
```

### Null policy

```csharp
// Cache "not found" results too (the default), or force a re-lookup on
// every call until the factory returns a value:
Customer? customer = await cache.HitAsync(
    $"customers:{id}",
    ct => FindCustomerAsync(id, ct),
    new DnaXCacheEntryOptions { CacheNulls = false },
    cancellationToken);
```

### Invalidation

```csharp
cache.Remove("prices:current");   // returns true if an entry was evicted
cache.Clear();                    // drops everything
```

### Type safety

A key always maps to one value type. Requesting `Hit<int>` for a key that
holds a `string` throws `DnaXCacheTypeMismatchException` instead of
returning a corrupted value.

### Synchronous call sites

```csharp
ExchangeRates rates = cache.Hit("fx:rates", LoadRates,
    new DnaXCacheEntryOptions { Duration = TimeSpan.FromMinutes(15) });
```

## DnaX.Redis.StackExchangeRedis — named Redis closures

An opt-in adapter: install it only in applications that use Redis. Each
named connection creates its multiplexer once, on first use, and disposes
it with the host.

### Registration

```csharp
builder.Services.AddDnaXRedis()
    .AddRedis("Session", builder.Configuration.GetConnectionString("Redis")!, database: 1)
    .AddRedis("Leaderboard", ConfigurationOptions.Parse("redis-lb:6379,abortConnect=false"));

// Or bring your own multiplexer (ownsMultiplexer: false leaves disposal to you):
builder.Services.AddDnaXRedis()
    .AddRedis("Shared", existingMultiplexer, database: 0);
```

### Usage

Unknown names throw `DnaXRedisNotFoundException` listing what is
registered.

```csharp
public sealed class SessionStore(IDnaXRedis redis)
{
    public ValueTask<string?> GetAsync(string sessionId, CancellationToken cancellationToken)
        => redis.ExecuteAsync(
            "Session",
            async (database, _) => (string?)await database.StringGetAsync($"session:{sessionId}"),
            cancellationToken);

    public ValueTask TouchAsync(string sessionId, CancellationToken cancellationToken)
        => redis.ExecuteAsync(
            "Session",
            async (database, _) =>
            {
                await database.KeyExpireAsync($"session:{sessionId}", TimeSpan.FromMinutes(20));
            },
            cancellationToken);
}
```

## DnaX.Diagnostics — health and runtime endpoints

Maps `/_dna/live`, `/_dna/ready`, and (opt-in) `/_dna/details` onto the
existing ASP.NET Core host. Authorization is required by default; details
and route listing are explicit opt-ins because they expose runtime
information.

```csharp
builder.Services.AddDnaXDiagnostics(options =>
{
    options.EnableDetails = true;              // maps the details endpoint
    options.IncludeRoutes = true;              // includes route patterns in details
    options.AuthorizationPolicy = "Operations"; // named policy; null = default policy
});

var app = builder.Build();

app.MapDnaXDiagnostics();            // default prefix: /_dna
// app.MapDnaXDiagnostics("/ops");   // or choose your own prefix
```

For local development only, authorization can be switched off:

```csharp
builder.Services.AddDnaXDiagnostics(options =>
    options.RequireAuthorization = builder.Environment.IsProduction());
```

## DnaX.Compatibility — golden legacy contracts

Contains only behavior-locked contracts from the original DNA library,
covered by golden tests. Use it when new code must agree with values
already persisted by legacy systems.

```csharp
using DnaX.Compatibility;

// Identical output to the legacy DNA Knuth hash — safe to compare against
// hashes stored years ago.
ulong single = LegacyHash.Knuth("customer-42");
ulong composite = LegacyHash.Knuth("customer", "42", region);  // nulls treated as ""
```

## Putting it together

A minimal web application using several packages side by side (see
[samples/DnaX.Sample.Web](samples/DnaX.Sample.Web/Program.cs) for the
runnable version):

```csharp
using DnaX.Caching;
using DnaX.Diagnostics;
using DnaX.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDnaXHosting(options => options.WritableDataRoot = "data");
builder.Services.AddDnaXCaching();
builder.Services.AddDnaXDiagnostics();

WebApplication app = builder.Build();

app.MapGet("/", async (IDnaXCache cache, IDnaXPaths paths, CancellationToken cancellationToken) =>
{
    string message = await cache.HitAsync(
        "sample:message",
        _ => new ValueTask<string>("DNA X is running"),
        cancellationToken: cancellationToken);

    return new { message, paths.ContentRoot, paths.WritableDataRoot };
});

app.MapDnaXDiagnostics();
app.Run();
```
