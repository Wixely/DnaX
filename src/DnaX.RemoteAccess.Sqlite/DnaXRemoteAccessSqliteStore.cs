using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace DnaX.RemoteAccess.Sqlite;

internal sealed class DnaXRemoteAccessSqliteStore(
    IServiceProvider services,
    Func<IServiceProvider, DbConnection> connectionFactory) : IDnaXRemoteAccessStore
{
    public async ValueTask<DnaXRemoteSurfaceState?> GetSurfaceAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, null, """
            SELECT DeploymentId, IsActive, AllowAnonymous, ProtectedRoute, RouteSuffix, Version, UpdatedAtUtc
            FROM DnaXRemoteSurfaces
            WHERE Surface = @surface;
            """);
        Add(command, "@surface", surface.ToString());
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DnaXRemoteSurfaceState(
            surface,
            reader.GetString(0),
            reader.GetInt64(1) != 0,
            reader.GetInt64(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            ParseDate(reader.GetString(6)));
    }

    public async ValueTask<DnaXRemoteStoredCredential?> GetCredentialAsync(
        DnaXRemoteSurface surface,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, null, """
            SELECT CredentialId, Algorithm, SecretHash, Suffix, ScopesJson, CreatedAtUtc, ExpiresAtUtc
            FROM DnaXRemoteCredentials
            WHERE Surface = @surface AND RevokedAtUtc IS NULL;
            """);
        Add(command, "@surface", surface.ToString());
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DnaXRemoteStoredCredential(
            Guid.Parse(reader.GetString(0)),
            surface,
            reader.GetString(1),
            (byte[])reader.GetValue(2),
            reader.GetString(3),
            JsonSerializer.Deserialize(reader.GetString(4), DnaXRemoteAccessSqliteJsonContext.Default.StringArray) ?? [],
            ParseDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)));
    }

    public async ValueTask<bool> TryReplaceSurfaceAsync(
        DnaXRemoteSurfaceState state,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSurfaceAsync(connection, transaction, state.Surface, state.DeploymentId, cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, transaction, """
            UPDATE DnaXRemoteSurfaces
            SET IsActive = @active,
                DeploymentId = @deploymentId,
                AllowAnonymous = @anonymous,
                ProtectedRoute = @route,
                RouteSuffix = @suffix,
                Version = @newVersion,
                UpdatedAtUtc = @updated
            WHERE Surface = @surface AND DeploymentId = @deploymentId AND Version = @expectedVersion;
            """);
        Add(command, "@active", state.IsActive ? 1 : 0);
        Add(command, "@deploymentId", state.DeploymentId);
        Add(command, "@anonymous", state.AllowAnonymous ? 1 : 0);
        Add(command, "@route", state.ProtectedRoute);
        Add(command, "@suffix", state.RouteSuffix);
        Add(command, "@newVersion", state.Version);
        Add(command, "@updated", FormatDate(state.UpdatedAtUtc));
        Add(command, "@surface", state.Surface.ToString());
        Add(command, "@expectedVersion", expectedVersion);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<bool> TryReplaceCredentialAsync(
        DnaXRemoteStoredCredential credential,
        string deploymentId,
        long expectedSurfaceVersion,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSurfaceAsync(connection, transaction, credential.Surface, deploymentId, cancellationToken).ConfigureAwait(false);
        await using DbCommand version = Command(connection, transaction, """
            UPDATE DnaXRemoteSurfaces
            SET Version = Version + 1, UpdatedAtUtc = @updated
            WHERE Surface = @surface AND DeploymentId = @deploymentId AND Version = @expectedVersion;
            """);
        Add(version, "@updated", FormatDate(credential.CreatedAtUtc));
        Add(version, "@surface", credential.Surface.ToString());
        Add(version, "@deploymentId", deploymentId);
        Add(version, "@expectedVersion", expectedSurfaceVersion);
        if (await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using DbCommand revoke = Command(connection, transaction, """
            UPDATE DnaXRemoteCredentials
            SET RevokedAtUtc = @revoked
            WHERE Surface = @surface AND RevokedAtUtc IS NULL;
            """);
        Add(revoke, "@revoked", FormatDate(credential.CreatedAtUtc));
        Add(revoke, "@surface", credential.Surface.ToString());
        await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand insert = Command(connection, transaction, """
            INSERT INTO DnaXRemoteCredentials
                (CredentialId, Surface, Algorithm, SecretHash, Suffix, ScopesJson, CreatedAtUtc, ExpiresAtUtc, RevokedAtUtc)
            VALUES
                (@id, @surface, @algorithm, @hash, @suffix, @scopes, @created, @expires, NULL);
            """);
        Add(insert, "@id", credential.Id.ToString("D"));
        Add(insert, "@surface", credential.Surface.ToString());
        Add(insert, "@algorithm", credential.Algorithm);
        Add(insert, "@hash", credential.Hash);
        Add(insert, "@suffix", credential.Suffix);
        Add(insert, "@scopes", JsonSerializer.Serialize(
            credential.Scopes.ToArray(),
            DnaXRemoteAccessSqliteJsonContext.Default.StringArray));
        Add(insert, "@created", FormatDate(credential.CreatedAtUtc));
        Add(insert, "@expires", credential.ExpiresAtUtc is null ? null : FormatDate(credential.ExpiresAtUtc.Value));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> TryRevokeCredentialAsync(
        DnaXRemoteSurface surface,
        string deploymentId,
        long expectedSurfaceVersion,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSurfaceAsync(connection, transaction, surface, deploymentId, cancellationToken).ConfigureAwait(false);
        await using DbCommand version = Command(connection, transaction, """
            UPDATE DnaXRemoteSurfaces
            SET IsActive = 0,
                AllowAnonymous = 0,
                Version = Version + 1,
                UpdatedAtUtc = @updated
            WHERE Surface = @surface AND DeploymentId = @deploymentId AND Version = @expectedVersion;
            """);
        Add(version, "@updated", FormatDate(revokedAtUtc));
        Add(version, "@surface", surface.ToString());
        Add(version, "@deploymentId", deploymentId);
        Add(version, "@expectedVersion", expectedSurfaceVersion);
        if (await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using DbCommand revoke = Command(connection, transaction, """
            UPDATE DnaXRemoteCredentials
            SET RevokedAtUtc = @revoked
            WHERE Surface = @surface AND RevokedAtUtc IS NULL;
            """);
        Add(revoke, "@revoked", FormatDate(revokedAtUtc));
        Add(revoke, "@surface", surface.ToString());
        await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask RecordAuditAsync(
        DnaXRemoteAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, null, """
            INSERT INTO DnaXRemoteAuditEvents
                (Surface, ServiceIdentity, CredentialSuffix, Client, Method, Action, Result, StatusCode, CorrelationId, OccurredAtUtc)
            VALUES
                (@surface, @identity, @suffix, @client, @method, @action, @result, @status, @correlation, @occurred);
            """);
        Add(command, "@surface", auditEvent.Surface.ToString());
        Add(command, "@identity", auditEvent.ServiceIdentity);
        Add(command, "@suffix", auditEvent.CredentialSuffix);
        Add(command, "@client", auditEvent.Client);
        Add(command, "@method", auditEvent.Method);
        Add(command, "@action", auditEvent.Action);
        Add(command, "@result", auditEvent.Result.ToString());
        Add(command, "@status", auditEvent.StatusCode);
        Add(command, "@correlation", auditEvent.CorrelationId);
        Add(command, "@occurred", FormatDate(auditEvent.OccurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DnaXRemoteAuditRecord>> GetRecentAuditAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, null, """
            SELECT Id, Surface, ServiceIdentity, CredentialSuffix, Client, Method, Action,
                   Result, StatusCode, CorrelationId, OccurredAtUtc
            FROM DnaXRemoteAuditEvents
            ORDER BY Id DESC
            LIMIT @maximumCount;
            """);
        Add(command, "@maximumCount", maximumCount);
        List<DnaXRemoteAuditRecord> records = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DnaXRemoteAuditEvent auditEvent = new(
                Enum.Parse<DnaXRemoteSurface>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                Enum.Parse<DnaXRemoteAuditResult>(reader.GetString(7)),
                reader.GetInt32(8),
                reader.GetString(9),
                ParseDate(reader.GetString(10)));
            records.Add(new DnaXRemoteAuditRecord(reader.GetInt64(0), auditEvent));
        }

        return records;
    }

    public async ValueTask PruneAuditAsync(
        DateTimeOffset retainAfterUtc,
        int maximumEvents,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand byTime = Command(connection, transaction, """
            DELETE FROM DnaXRemoteAuditEvents WHERE OccurredAtUtc < @retainAfter;
            """);
        Add(byTime, "@retainAfter", FormatDate(retainAfterUtc));
        await byTime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand byCount = Command(connection, transaction, """
            DELETE FROM DnaXRemoteAuditEvents
            WHERE Id NOT IN (
                SELECT Id FROM DnaXRemoteAuditEvents ORDER BY Id DESC LIMIT @maximumEvents
            );
            """);
        Add(byCount, "@maximumEvents", maximumEvents);
        await byCount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = connectionFactory(services);
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask EnsureSurfaceAsync(
        DbConnection connection,
        DbTransaction transaction,
        DnaXRemoteSurface surface,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = Command(connection, transaction, """
            INSERT OR IGNORE INTO DnaXRemoteSurfaces
                (Surface, DeploymentId, IsActive, AllowAnonymous, ProtectedRoute, RouteSuffix, Version, UpdatedAtUtc)
            VALUES
                (@surface, @deploymentId, 0, 0, NULL, NULL, 0, @updated);
            """);
        Add(command, "@surface", surface.ToString());
        Add(command, "@deploymentId", deploymentId);
        Add(command, "@updated", FormatDate(DateTimeOffset.UnixEpoch));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
