using System.Data;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnaX.Data.Migrations;

internal sealed class DnaXDatabaseMigrator : IDnaXDatabaseMigrator
{
    private readonly IReadOnlyDictionary<string, DnaXMigrationRegistration> _registrations;
    private readonly IServiceProvider _services;
    private readonly ILogger<DnaXDatabaseMigrator> _logger;
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    public DnaXDatabaseMigrator(
        IEnumerable<DnaXMigrationRegistration> registrations,
        IServiceProvider services,
        ILogger<DnaXDatabaseMigrator>? logger = null)
    {
        _services = services;
        _logger = logger ?? NullLogger<DnaXDatabaseMigrator>.Instance;

        Dictionary<string, DnaXMigrationRegistration> byName = new(StringComparer.Ordinal);
        foreach (DnaXMigrationRegistration registration in registrations)
        {
            if (!byName.TryAdd(registration.Name, registration))
            {
                throw new InvalidOperationException(
                    $"Migration database '{registration.Name}' is registered more than once.");
            }
        }

        _registrations = byName;
    }

    public async ValueTask<DnaXMigrationStatus> GetStatusAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        DnaXMigrationRegistration registration = GetRegistration(name);
        await using DbConnection connection = CreateConnection(registration);
        await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await registration.Adapter.InitializeConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using IDnaXMigrationSession session = await registration.Adapter
            .AcquireSessionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await registration.Adapter.EnsureLedgerAsync(connection, session.Transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DnaXAppliedMigration> applied = await registration.Adapter
                .ReadLedgerAsync(connection, session.Transaction, cancellationToken)
                .ConfigureAwait(false);
            DnaXMigrationStatus status = Evaluate(registration.Manifest, applied);
            LogLegacyChecksumAcceptance(name, registration.Manifest, applied);
            if (status.State == DnaXMigrationState.Pending && _failures.TryGetValue(name, out string? failure))
            {
                status = status with
                {
                    State = DnaXMigrationState.Failed,
                    Issues = status.Issues.Concat([failure]).ToArray(),
                };
            }
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch
        {
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<DnaXMigrationResult> MigrateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        DnaXMigrationRegistration registration = GetRegistration(name);
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = DnaXMigrationDiagnostics.ActivitySource.StartActivity("database.migrate");
        activity?.SetTag("db.system", registration.Adapter.ProviderName);
        activity?.SetTag("dnax.database.name", registration.Name);
        activity?.SetTag("dnax.schema.target_version", registration.Manifest.CurrentVersion);
        activity?.SetTag("dnax.migration.atomicity", registration.Adapter.Atomicity.ToString());

        await using DbConnection connection = CreateConnection(registration);
        await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await registration.Adapter.InitializeConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using IDnaXMigrationSession session = await registration.Adapter
            .AcquireSessionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        DnaXMigration? activeMigration = null;
        List<DnaXMigration> appliedMigrations = [];
        try
        {
            await registration.Adapter.EnsureLedgerAsync(connection, session.Transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DnaXAppliedMigration> applied = await registration.Adapter
                .ReadLedgerAsync(connection, session.Transaction, cancellationToken)
                .ConfigureAwait(false);
            DnaXMigrationStatus status = Evaluate(registration.Manifest, applied);
            LogLegacyChecksumAcceptance(name, registration.Manifest, applied);

            if (!status.CanMigrate)
            {
                activity?.SetStatus(ActivityStatusCode.Error, status.State.ToString());
                throw new DnaXMigrationValidationException(name, status);
            }

            if (status.PendingMigrations.Count == 0)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                _failures.TryRemove(name, out _);
                _logger.LogDebug(
                    "Database {DatabaseName} is already at schema version {SchemaVersion}.",
                    name,
                    status.CurrentVersion);
                return new(
                    name,
                    status,
                    appliedMigrations.AsReadOnly(),
                    Stopwatch.GetElapsedTime(started),
                    registration.Adapter.Atomicity);
            }

            if (registration.BeforeMigrateAsync is not null)
            {
                await registration.BeforeMigrateAsync(
                    new(name, registration.Manifest, status, connection, session.Transaction),
                    cancellationToken).ConfigureAwait(false);
            }

            if (registration.Adapter.Atomicity == DnaXMigrationAtomicity.ProviderManagedCheckpoints)
            {
                _logger.LogWarning(
                    "Database {DatabaseName} uses provider-managed migration checkpoints; the complete pending chain is not atomic.",
                    name);
            }

            foreach (DnaXMigration migration in status.PendingMigrations)
            {
                activeMigration = migration;
                _logger.LogInformation(
                    "Applying database migration {MigrationVersion} ({MigrationId}): {MigrationName} to {DatabaseName}.",
                    migration.Version,
                    migration.Id,
                    migration.Name,
                    name);

                await migration.Operation(connection, session.Transaction, cancellationToken).ConfigureAwait(false);
                DateTimeOffset appliedAt = registration.TimeProvider.GetUtcNow();
                await registration.Adapter.RecordAppliedAsync(
                    connection,
                    session.Transaction,
                    new(
                        migration.Version,
                        migration.Id,
                        migration.Name,
                        migration.Checksum,
                        registration.ApplicationVersion,
                        appliedAt),
                    cancellationToken).ConfigureAwait(false);
                await session.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                appliedMigrations.Add(migration);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            _failures.TryRemove(name, out _);
            DnaXMigrationStatus finalStatus = new(
                DnaXMigrationState.Current,
                registration.Manifest.CurrentVersion,
                registration.Manifest.CurrentVersion,
                Array.Empty<DnaXMigration>(),
                Array.Empty<string>());
            activity?.SetTag("dnax.migrations.applied", appliedMigrations.Count);
            _logger.LogInformation(
                "Database {DatabaseName} reached schema version {SchemaVersion} after applying {MigrationCount} migrations.",
                name,
                finalStatus.CurrentVersion,
                appliedMigrations.Count);
            return new(
                name,
                finalStatus,
                appliedMigrations.AsReadOnly(),
                Stopwatch.GetElapsedTime(started),
                registration.Adapter.Atomicity);
        }
        catch (DnaXMigrationValidationException)
        {
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            _logger.LogError(
                exception,
                "Database migration failed for {DatabaseName} at migration version {MigrationVersion} ({MigrationId}).",
                name,
                activeMigration?.Version,
                activeMigration?.Id);

            if (activeMigration is not null)
            {
                _failures[name] =
                    $"Migration {activeMigration.Version} ('{activeMigration.Id}') failed during this process lifetime.";
                throw new DnaXMigrationFailedException(name, activeMigration, exception);
            }

            throw new DnaXMigrationException(name, $"Migration preparation failed for database '{name}'.", exception);
        }
    }

    public async ValueTask<DnaXMigrationBaselineResult> BaselineAsync(
        string name,
        int throughVersion,
        Func<DnaXBaselineVerificationContext, CancellationToken, ValueTask> verifyExistingSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifyExistingSchema);
        DnaXMigrationRegistration registration = GetRegistration(name);
        if (throughVersion < 1 || throughVersion > registration.Manifest.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughVersion),
                $"The baseline version must be between 1 and {registration.Manifest.CurrentVersion}.");
        }

        long started = Stopwatch.GetTimestamp();
        using Activity? activity = DnaXMigrationDiagnostics.ActivitySource.StartActivity("database.baseline");
        activity?.SetTag("db.system", registration.Adapter.ProviderName);
        activity?.SetTag("dnax.database.name", registration.Name);
        activity?.SetTag("dnax.schema.baseline_version", throughVersion);

        await using DbConnection connection = CreateConnection(registration);
        await OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        await registration.Adapter.InitializeConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using IDnaXMigrationSession session = await registration.Adapter
            .AcquireSessionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await registration.Adapter.EnsureLedgerAsync(connection, session.Transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DnaXAppliedMigration> existing = await registration.Adapter
                .ReadLedgerAsync(connection, session.Transaction, cancellationToken)
                .ConfigureAwait(false);
            if (existing.Count != 0)
            {
                throw new DnaXMigrationException(
                    name,
                    $"Database '{name}' cannot be baselined because its DnaX migration ledger is not empty.");
            }

            await verifyExistingSchema(
                new(name, throughVersion, registration.Manifest, connection, session.Transaction),
                cancellationToken).ConfigureAwait(false);

            DateTimeOffset appliedAt = registration.TimeProvider.GetUtcNow();
            foreach (DnaXMigration migration in registration.Manifest.Migrations.Take(throughVersion))
            {
                await registration.Adapter.RecordAppliedAsync(
                    connection,
                    session.Transaction,
                    new(
                        migration.Version,
                        migration.Id,
                        migration.Name,
                        migration.Checksum,
                        registration.ApplicationVersion,
                        appliedAt),
                    cancellationToken).ConfigureAwait(false);
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            _failures.TryRemove(name, out _);
            DnaXMigration[] pending = registration.Manifest.Migrations.Skip(throughVersion).ToArray();
            DnaXMigrationStatus status = new(
                pending.Length == 0 ? DnaXMigrationState.Current : DnaXMigrationState.Pending,
                registration.Manifest.CurrentVersion,
                throughVersion,
                pending,
                Array.Empty<string>());
            _logger.LogWarning(
                "Baselined existing database {DatabaseName} through schema version {SchemaVersion} after application verification.",
                name,
                throughVersion);
            return new(
                name,
                throughVersion,
                status,
                Stopwatch.GetElapsedTime(started),
                registration.Adapter.Atomicity);
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().FullName);
            await TryRollbackAsync(session, name, _logger).ConfigureAwait(false);
            if (exception is DnaXMigrationException)
            {
                throw;
            }

            throw new DnaXMigrationException(
                name,
                $"Baseline verification failed for database '{name}'. No baseline entries were recorded.",
                exception);
        }
    }

    internal static DnaXMigrationStatus Evaluate(
        DnaXMigrationManifest manifest,
        IReadOnlyList<DnaXAppliedMigration> applied)
    {
        List<string> issues = [];
        List<DnaXChecksumDrift> drifts = [];
        HashSet<int> versions = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        int databaseVersion = applied.Count == 0 ? 0 : applied.Max(item => item.Version);
        bool future = false;

        foreach (DnaXAppliedMigration item in applied)
        {
            if (!versions.Add(item.Version))
            {
                issues.Add($"Ledger version {item.Version} is duplicated.");
            }

            if (!ids.Add(item.Id))
            {
                issues.Add($"Ledger identifier '{item.Id}' is duplicated.");
            }

            if (item.Version < 1)
            {
                issues.Add($"Ledger version {item.Version} is invalid; migration versions start at 1.");
                continue;
            }

            if (item.Version > manifest.CurrentVersion)
            {
                future = true;
                issues.Add(
                    $"Ledger version {item.Version} ('{item.Id}') is newer than supported version {manifest.CurrentVersion}.");
                continue;
            }

            DnaXMigration expected = manifest.Migrations[item.Version - 1];
            if (!string.Equals(item.Id, expected.Id, StringComparison.Ordinal))
            {
                issues.Add(
                    $"Version {item.Version} has identifier '{item.Id}' but the manifest declares '{expected.Id}'.");
            }

            if (!string.Equals(item.Name, expected.Name, StringComparison.Ordinal))
            {
                issues.Add(
                    $"Migration '{expected.Id}' has recorded name '{item.Name}' but the manifest declares '{expected.Name}'.");
            }

            if (!expected.MatchesRecordedChecksum(item.Checksum))
            {
                drifts.Add(new(item.Version, expected.Id, expected.Checksum, item.Checksum));
                issues.Add(
                    $"Migration '{expected.Id}' checksum differs from the applied ledger " +
                    $"(expected {Abbreviate(expected.Checksum)}, recorded {Abbreviate(item.Checksum)}). " +
                    "Applied migrations are immutable.");
            }
        }

        for (int version = 1; version <= Math.Min(databaseVersion, manifest.CurrentVersion); version++)
        {
            if (!versions.Contains(version))
            {
                issues.Add($"Migration history is missing version {version} before applied version {databaseVersion}.");
            }
        }

        DnaXMigration[] pending = manifest.Migrations
            .Where(migration => !versions.Contains(migration.Version))
            .ToArray();
        DnaXMigrationState state = future
            ? DnaXMigrationState.UnsupportedFuture
            : issues.Count > 0
                ? DnaXMigrationState.Drifted
                : pending.Length > 0
                    ? DnaXMigrationState.Pending
                    : DnaXMigrationState.Current;

        return new(state, manifest.CurrentVersion, databaseVersion, pending, issues.AsReadOnly())
        {
            ChecksumDrifts = drifts.AsReadOnly(),
        };
    }

    private static string Abbreviate(string checksum) =>
        checksum.Length <= 23 ? checksum : checksum[..23];

    /// <summary>
    /// Logs ledger rows accepted through a historical line-ending checksum variant. Without this
    /// an operator has no signal that a ledger predates checksum normalization, and the
    /// compatibility set could never be retired responsibly.
    /// </summary>
    private void LogLegacyChecksumAcceptance(
        string name,
        DnaXMigrationManifest manifest,
        IReadOnlyList<DnaXAppliedMigration> applied)
    {
        foreach (DnaXAppliedMigration item in applied)
        {
            if (item.Version < 1 || item.Version > manifest.CurrentVersion)
            {
                continue;
            }

            DnaXMigration expected = manifest.Migrations[item.Version - 1];
            if (!string.Equals(item.Checksum, expected.Checksum, StringComparison.Ordinal)
                && expected.MatchesRecordedChecksum(item.Checksum))
            {
                _logger.LogInformation(
                    "Migration {MigrationVersion} ('{MigrationId}') in database {DatabaseName} was recorded before "
                        + "checksum line-ending normalization and was accepted through a compatibility variant. "
                        + "The ledger row is left unchanged.",
                    item.Version,
                    expected.Id,
                    name);
            }
        }
    }

    private DnaXMigrationRegistration GetRegistration(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _registrations.TryGetValue(name, out DnaXMigrationRegistration? registration)
            ? registration
            : throw new DnaXMigrationDatabaseNotFoundException(name, _registrations.Keys);
    }

    private DbConnection CreateConnection(DnaXMigrationRegistration registration) =>
        registration.ConnectionFactory(_services)
        ?? throw new InvalidOperationException($"The connection factory for '{registration.Name}' returned null.");

    private static async ValueTask OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"The migration connection is in state '{connection.State}' after opening.");
        }
    }

    private static async ValueTask TryRollbackAsync(
        IDnaXMigrationSession session,
        string databaseName,
        ILogger logger)
    {
        try
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackException)
        {
            logger.LogError(rollbackException, "Rollback failed for migration database {DatabaseName}.", databaseName);
        }
    }
}
