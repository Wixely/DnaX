# Database manifests and SQLite migrations

`DnaX.Data.Migrations` is a schema-lifecycle library, not an ORM. Applications own their schema, SQL, repositories, backups, and data-repair policy. The library validates and runs an explicit ordered manifest, records immutable history, and reports operational state. Applications can continue using Dapper normally; none of the migration packages depends on Dapper.

## Packages and boundaries

- `DnaX.Data.Migrations` contains provider-neutral manifests, runner contracts, named DI registration, startup integration, status models, logging, and activities. It has no database-provider dependency.
- `DnaX.Data.Migrations.Sqlite` contains the tested SQLite adapter, ledger, locking, PRAGMA policy, and deterministic schema inspection.
- `DnaX.Data.Migrations.Sqlite.Testing` contains temporary-database scopes and historical-chain verification. Keep it in test projects.
- `DnaX.Data` remains unchanged and provider-neutral. An application may use it beside the migration packages, but neither requires the other.

Only SQLite is supported. The adapter boundary allows separately tested providers later; the current packages make no SQL Server, PostgreSQL, MySQL, or Oracle compatibility claim.

## Authoring a manifest

The manifest is ordinary application-owned C# and must be contiguous and explicitly ordered from version 1:

```csharp
using DnaX.Data.Migrations;

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

Each migration has an integer version/order, stable case-sensitive identifier, descriptive name, operation, and SHA-256 checksum. SQL checksums cover the exact UTF-8 string, including whitespace and line endings. Once a migration has reached a database, do not rename, reorder, edit, renumber, squash, or reuse it. Add a new migration instead. The runner fails on identifier, name, checksum, gap, duplicate, and future-version inconsistencies.

### Explicit embedded SQL

SQL files can remain separate while the manifest stays authoritative:

```xml
<ItemGroup>
  <EmbeddedResource Include="Data\Migrations\*.sql" />
</ItemGroup>
```

Name every resource explicitly:

```csharp
DnaXMigration.EmbeddedSql(
    3,
    "add-item-notes",
    "Add item notes",
    typeof(ApplicationSchema).Assembly,
    "MyApplication.Data.Migrations.0003_add_item_notes.sql")
```

There is no filename or reflection-order discovery. This keeps ordering reviewable and supports trimming and NativeAOT without dynamically discovering migration types.

### Code-backed migrations

Use a callback when SQL alone is genuinely insufficient. The caller supplies a stable checksum token so changing callback behavior is detectable:

```csharp
DnaXMigration.Code(
    4,
    "normalize-item-codes",
    "Normalize existing item codes",
    DnaXMigration.ComputeChecksum("normalize-item-codes-v1"),
    async (connection, transaction, cancellationToken) =>
    {
        // Use DbCommand or Dapper. Every command must use the supplied transaction.
    })
```

Treat that checksum token as immutable source-controlled migration content. Code callbacks should be deterministic, bounded startup schema work—not environment seed data, large backfills, or recurring repairs.

## Registration and startup

```csharp
using DnaX.Data.Migrations;
using DnaX.Data.Migrations.Sqlite;
using Microsoft.Data.Sqlite;

builder.Services.AddDnaXDataMigrations("Primary", options =>
{
    options.ConnectionFactory = _ => new SqliteConnection(connectionString);
    options.Manifest = ApplicationSchema.Manifest;
    options.ApplicationVersion = "1.4.0";
    options.UseSqlite(sqlite =>
    {
        sqlite.LockTimeout = TimeSpan.FromSeconds(30);
        sqlite.EnableWriteAheadLogging = true;
        sqlite.EnforceForeignKeys = true;
        sqlite.DeferForeignKeysDuringMigration = true;
    });
});

WebApplication app = builder.Build();
await app.Services.MigrateDnaXDatabaseAsync("Primary");
app.Run();
```

The factory may return an open or closed `Microsoft.Data.Sqlite.SqliteConnection`; the runner opens closed connections and always disposes them. Connection strings and SQL are never logged.

For automatic Generic Host startup migration, set `options.MigrateOnStartup = true`. The hosted service completes all opted-in named databases before host startup completes. Do not also call the explicit operation unless an intentional idempotency check is wanted.

## SQLite transaction and locking contract

For every status or migration operation the adapter:

1. opens a dedicated connection;
2. enables foreign keys and, by default, WAL journal mode;
3. acquires a database write lock with `BEGIN IMMEDIATE`;
4. creates or reads `__DnaXMigrations` while holding that lock;
5. validates the complete ledger against the manifest;
6. applies every pending migration and ledger insert in that one transaction;
7. commits the complete chain, or rolls the complete chain back on any failure.

This serializes application instances and processes sharing one SQLite database. A second runner waits and re-reads the ledger after acquiring the lock, so it skips work committed by the first. Lock retries honor the configured timeout and cancellation; an individual provider lock attempt is bounded to approximately one second before cancellation is re-observed.

Foreign keys are enforced. Checks are deferred until commit by default so reviewed SQLite table-rebuild migrations can copy, drop, rename, and reconnect tables atomically. A violation still prevents commit and rolls back the chain. Disable deferral only when every intermediate statement must satisfy constraints.

Because all pending versions are atomic, a failure at version 5 also rolls back versions 3 and 4 if all three were pending. No failed version is written to the ledger. The process remembers the sanitized last failure for diagnostics until a retry succeeds; after restart the durable state is correctly pending.

## Ledger and status

`__DnaXMigrations` stores version, identifier, name, checksum, optional application version, and applied UTC timestamp. It contains no SQL or application data.

```csharp
IDnaXDatabaseMigrator migrator = services.GetRequiredService<IDnaXDatabaseMigrator>();
DnaXMigrationStatus status = await migrator.GetStatusAsync("Primary", cancellationToken);
```

Status is `Current`, `Pending`, `Drifted`, `Failed`, or `UnsupportedFuture`. `Failed` is the most recent failed attempt in this process; `UnsupportedFuture` means the database is newer than the application. Use the API in an application-owned readiness check or administration view. Never automatically repair drift or a future database.

The runner emits structured `ILogger` events and activities from `DnaX.Data.Migrations`. Tags contain only provider, configured database name, target version, and applied count—not connection strings, SQL, parameters, or seed data.

## Backups

Configure `BeforeMigrateAsync` to call an application-owned, tested backup service after lock and validation but before migration SQL:

```csharp
options.BeforeMigrateAsync = async (context, cancellationToken) =>
    await backupService.CreateVerifiedBackupAsync(
        context.DatabaseName,
        context.Connection,
        context.Transaction,
        cancellationToken);
```

The hook runs inside the locked transaction and aborts migration if it throws. The library intentionally does not copy a SQLite file: a filesystem copy of a live WAL database is not inherently valid. Choose and restore-test an online SQLite backup or an operational stop-and-copy procedure appropriate to the service.

## Adopting an existing database

Do not run initial `CREATE TABLE` migrations over an existing Daybreak/Stashographer-style database. Use the explicit baseline operation once, after mapping the old schema and ledger to the new manifest:

```csharp
await migrator.BaselineAsync(
    "Primary",
    throughVersion: 5,
    async (context, cancellationToken) =>
    {
        // Query the legacy ledger and sqlite_schema using context.Connection
        // and context.Transaction. Throw unless every expected version and
        // schema object is proven equivalent through version 5.
    },
    cancellationToken);

await migrator.MigrateAsync("Primary", cancellationToken);
```

Baselining requires an application verification callback, works only with an empty DnaX ledger, holds the database lock, and rolls back all new rows if verification fails. It records manifest identities and checksums without executing operations. It never removes or changes the legacy ledger. Back up first, retain that ledger for audit, and test against restored production-shaped data before rollout.

## Migration tests

Reference `DnaX.Data.Migrations.Sqlite.Testing` only from the test project.

### Isolated current database

```csharp
await using DnaXSqliteMigrationTestScope database =
    await DnaXSqliteMigrationTestScope.CreateAsync(ApplicationSchema.Manifest);

await using SqliteConnection connection = await database.OpenConnectionAsync();
// Run repository tests against connection.
```

The scope uses a unique temporary directory, disables pooling, migrates before returning, and deletes the database on async disposal.

### Historical data-preservation fixture

```csharp
await using DnaXSqliteMigrationTestScope database =
    await DnaXSqliteMigrationTestScope.CreateHistoricalAsync(
        ApplicationSchema.Manifest,
        initialVersion: 3);

await using (SqliteConnection connection = await database.OpenConnectionAsync())
{
    // Insert representative version-3 data here.
}

await database.UpgradeAsync();
// Assert data, foreign keys, indexes, and triggers survived.
```

### Every historical version in CI

```csharp
DnaXHistoricalMigrationVerification verification =
    await DnaXSqliteMigrationVerifier.VerifyAllHistoricalVersionsAsync(
        ApplicationSchema.Manifest);
```

The verifier builds a canonical fresh database, materializes every version from 0 through current minus one, upgrades each, and requires its sanitized `sqlite_schema` snapshot to match the canonical snapshot. Keep representative data-preservation tests too; schema equality cannot prove data semantics.

## Deliberate non-goals

- no entity tracking, repository generation, or query abstraction;
- no inferred or automatically approved destructive changes;
- no seed/demo data or long-running operational repair jobs;
- no automatic history rewrite, squash, checksum acceptance, or drift repair;
- no provider claims beyond the tested SQLite adapter;
- no CLI, code generator, Node.js, or Python toolchain.
