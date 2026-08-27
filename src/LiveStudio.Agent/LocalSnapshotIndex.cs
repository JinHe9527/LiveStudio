using Microsoft.Data.Sqlite;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed record LocalSnapshotRecord(
    Guid Id,
    string Name,
    string PackagePath,
    string Sha256,
    long Length,
    DateTimeOffset CreatedAt,
    bool Uploaded,
    bool UploadEligible,
    Guid? RoomId = null);

public sealed record TrustedPackageSigner(
    string KeyId,
    string PublicKeyPem,
    string FingerprintSha256,
    DateTimeOffset TrustedAt);

public sealed record LocalOperationRecord(
    Guid Id,
    LocalOperationKind Kind,
    LocalOperationStatus Status,
    string Message,
    Guid? SnapshotId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record PendingJobEvent(
    Guid Id,
    Guid JobId,
    Guid ExecutionId,
    long Sequence,
    JobStatus Status,
    string Message,
    string? DetailCode,
    string? VerificationDetail,
    DateTimeOffset CreatedAt);

public sealed class LocalSnapshotIndex
{
    private readonly string connectionString;

    public LocalSnapshotIndex()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio"))
    {
    }

    public LocalSnapshotIndex(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "index.db"),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                package_path TEXT NOT NULL UNIQUE,
                sha256 TEXT NOT NULL,
                length INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                uploaded INTEGER NOT NULL CHECK (uploaded IN (0, 1)),
                upload_eligible INTEGER NOT NULL DEFAULT 1 CHECK (upload_eligible IN (0, 1)),
                room_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_snapshots_uploaded_created_at
                ON snapshots(uploaded, created_at);
            CREATE TABLE IF NOT EXISTS device_mappings (
                id TEXT PRIMARY KEY,
                organization_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                source_logical_id TEXT NOT NULL,
                application INTEGER NOT NULL,
                target_device_id TEXT NOT NULL,
                target_source_name TEXT NOT NULL,
                target_scene_name TEXT NOT NULL,
                create_source_when_missing INTEGER NOT NULL CHECK (create_source_when_missing IN (0, 1)),
                UNIQUE(device_id, source_logical_id, application)
            );
            CREATE TABLE IF NOT EXISTS trusted_signers (
                key_id TEXT PRIMARY KEY,
                public_key_pem TEXT NOT NULL,
                fingerprint_sha256 TEXT NOT NULL UNIQUE,
                trusted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS local_operations (
                id TEXT PRIMARY KEY,
                kind INTEGER NOT NULL,
                status INTEGER NOT NULL,
                message TEXT NOT NULL,
                snapshot_id TEXT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_local_operations_started_at
                ON local_operations(started_at DESC);
            CREATE TABLE IF NOT EXISTS job_event_outbox (
                id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                execution_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                status INTEGER NOT NULL,
                message TEXT NOT NULL,
                detail_code TEXT NULL,
                verification_detail TEXT NULL,
                created_at TEXT NOT NULL,
                uploaded INTEGER NOT NULL DEFAULT 0 CHECK (uploaded IN (0, 1)),
                UNIQUE(job_id, execution_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_job_event_outbox_pending
                ON job_event_outbox(uploaded, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureUploadEligibleColumnAsync(connection, cancellationToken);
        await EnsureRoomIdColumnAsync(connection, cancellationToken);
        var interrupted = connection.CreateCommand();
        interrupted.CommandText = """
            UPDATE local_operations
            SET status = $failed,
                message = 'Agent 在操作完成前退出',
                completed_at = $completedAt
            WHERE status = $running;
            """;
        interrupted.Parameters.AddWithValue("$failed", (int)LocalOperationStatus.Failed);
        interrupted.Parameters.AddWithValue("$running", (int)LocalOperationStatus.Running);
        interrupted.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        await interrupted.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveOperationAsync(
        LocalOperationRecord operation,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_operations(
                id, kind, status, message, snapshot_id, started_at, completed_at)
            VALUES (
                $id, $kind, $status, $message, $snapshotId, $startedAt, $completedAt)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                message = excluded.message,
                snapshot_id = excluded.snapshot_id,
                completed_at = excluded.completed_at;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)operation.Kind);
        command.Parameters.AddWithValue("$status", (int)operation.Status);
        command.Parameters.AddWithValue("$message", operation.Message);
        command.Parameters.AddWithValue(
            "$snapshotId",
            operation.SnapshotId is { } snapshotId ? snapshotId.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", operation.StartedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$completedAt",
            operation.CompletedAt is { } completedAt ? completedAt.ToString("O") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueJobEventAsync(PendingJobEvent jobEvent, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_event_outbox(
                id, job_id, execution_id, sequence, status, message,
                detail_code, verification_detail, created_at, uploaded)
            VALUES (
                $id, $jobId, $executionId, $sequence, $status, $message,
                $detailCode, $verificationDetail, $createdAt, 0);
            """;
        command.Parameters.AddWithValue("$id", jobEvent.Id.ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobEvent.JobId.ToString("N"));
        command.Parameters.AddWithValue("$executionId", jobEvent.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$sequence", jobEvent.Sequence);
        command.Parameters.AddWithValue("$status", (int)jobEvent.Status);
        command.Parameters.AddWithValue("$message", jobEvent.Message);
        command.Parameters.AddWithValue("$detailCode", (object?)jobEvent.DetailCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$verificationDetail", (object?)jobEvent.VerificationDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", jobEvent.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingJobEvent>> GetPendingJobEventsAsync(CancellationToken cancellationToken)
    {
        var events = new List<PendingJobEvent>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, execution_id, sequence, status, message,
                   detail_code, verification_detail, created_at
            FROM job_event_outbox
            WHERE uploaded = 0
            ORDER BY created_at, sequence
            LIMIT 200;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new PendingJobEvent(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                reader.GetInt64(3),
                (JobStatus)reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return events;
    }

    public async Task MarkJobEventUploadedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_event_outbox SET uploaded = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalOperationRecord>> GetOperationsAsync(
        CancellationToken cancellationToken)
    {
        var operations = new List<LocalOperationRecord>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, status, message, snapshot_id, started_at, completed_at
            FROM local_operations
            ORDER BY started_at DESC
            LIMIT 200;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            operations.Add(new LocalOperationRecord(
                Guid.ParseExact(reader.GetString(0), "N"),
                (LocalOperationKind)reader.GetInt32(1),
                (LocalOperationStatus)reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.ParseExact(reader.GetString(4), "N"),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(6)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return operations;
    }

    public async Task SaveAsync(LocalSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SaveSnapshotAsync(connection, null, snapshot, cancellationToken);
    }

    public async Task SaveSnapshotWithMappingsAsync(
        LocalSnapshotRecord snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SaveSnapshotAsync(connection, transaction, snapshot, cancellationToken);
        foreach (var mapping in mappings)
        {
            await SaveMappingAsync(connection, transaction, mapping, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SaveSnapshotAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        LocalSnapshotRecord snapshot,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            INSERT INTO snapshots(id, name, package_path, sha256, length, created_at, uploaded, upload_eligible, room_id)
            VALUES ($id, $name, $path, $sha256, $length, $createdAt, $uploaded, $uploadEligible, $roomId)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                package_path = excluded.package_path,
                sha256 = excluded.sha256,
                length = excluded.length,
                uploaded = excluded.uploaded,
                upload_eligible = excluded.upload_eligible,
                room_id = excluded.room_id;
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", snapshot.Name);
        command.Parameters.AddWithValue("$path", snapshot.PackagePath);
        command.Parameters.AddWithValue("$sha256", snapshot.Sha256);
        command.Parameters.AddWithValue("$length", snapshot.Length);
        command.Parameters.AddWithValue("$createdAt", snapshot.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$uploaded", snapshot.Uploaded ? 1 : 0);
        command.Parameters.AddWithValue("$uploadEligible", snapshot.UploadEligible ? 1 : 0);
        command.Parameters.AddWithValue(
            "$roomId",
            snapshot.RoomId is { } roomId ? roomId.ToString("N") : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalSnapshotRecord>> GetPendingUploadsAsync(CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT id, name, package_path, sha256, length, created_at, uploaded, upload_eligible, room_id
            FROM snapshots
            WHERE uploaded = 0 AND upload_eligible = 1
            ORDER BY created_at
            LIMIT 20;
            """,
            cancellationToken);
    }

    public async Task<IReadOnlyList<LocalSnapshotRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await QueryAsync(
            """
            SELECT id, name, package_path, sha256, length, created_at, uploaded, upload_eligible, room_id
            FROM snapshots
            ORDER BY created_at DESC;
            """,
            cancellationToken);
    }

    public async Task<LocalSnapshotRecord?> FindAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, package_path, sha256, length, created_at, uploaded, upload_eligible, room_id
            FROM snapshots
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", snapshotId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", snapshotId.ToString("N"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> RenameAsync(
        Guid snapshotId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$id", snapshotId.ToString("N"));
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots;";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<LocalSnapshotRecord>> QueryAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<LocalSnapshotRecord>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(ReadRecord(reader));
        }

        return snapshots;
    }

    private static LocalSnapshotRecord ReadRecord(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
        reader.GetInt64(6) == 1,
        reader.GetInt64(7) == 1,
        reader.IsDBNull(8) ? null : Guid.ParseExact(reader.GetString(8), "N"));

    public async Task MarkUploadedAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET uploaded = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", snapshotId.ToString("N"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"本地存档索引中找不到 {snapshotId}");
        }
    }

    public async Task SaveMappingAsync(DeviceMapping mapping, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SaveMappingAsync(connection, null, mapping, cancellationToken);
    }

    private static async Task SaveMappingAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        DeviceMapping mapping,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = """
            INSERT INTO device_mappings(
                id, organization_id, device_id, source_logical_id, application,
                target_device_id, target_source_name, target_scene_name, create_source_when_missing)
            VALUES (
                $id, $organizationId, $deviceId, $sourceLogicalId, $application,
                $targetDeviceId, $targetSourceName, $targetSceneName, $createSourceWhenMissing)
            ON CONFLICT(device_id, source_logical_id, application) DO UPDATE SET
                id = excluded.id,
                organization_id = excluded.organization_id,
                target_device_id = excluded.target_device_id,
                target_source_name = excluded.target_source_name,
                target_scene_name = excluded.target_scene_name,
                create_source_when_missing = excluded.create_source_when_missing;
            """;
        command.Parameters.AddWithValue("$id", mapping.Id.ToString("N"));
        command.Parameters.AddWithValue("$organizationId", mapping.OrganizationId.ToString("N"));
        command.Parameters.AddWithValue("$deviceId", mapping.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("$sourceLogicalId", mapping.SourceLogicalId.ToString("N"));
        command.Parameters.AddWithValue("$application", (int)mapping.Application);
        command.Parameters.AddWithValue("$targetDeviceId", mapping.TargetDeviceId);
        command.Parameters.AddWithValue("$targetSourceName", mapping.TargetSourceName);
        command.Parameters.AddWithValue("$targetSceneName", mapping.TargetSceneName);
        command.Parameters.AddWithValue("$createSourceWhenMissing", mapping.CreateSourceWhenMissing ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceMapping>> GetMappingsAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var mappings = new List<DeviceMapping>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, organization_id, device_id, source_logical_id, application,
                   target_device_id, target_source_name, target_scene_name, create_source_when_missing
            FROM device_mappings
            WHERE device_id = $deviceId
            ORDER BY application, target_source_name;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mappings.Add(new DeviceMapping(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                Guid.ParseExact(reader.GetString(3), "N"),
                (ApplicationKind)reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8) == 1));
        }

        return mappings;
    }

    public async Task SaveTrustedSignerAsync(
        TrustedPackageSigner signer,
        CancellationToken cancellationToken)
    {
        var existing = await FindTrustedSignerAsync(signer.KeyId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.FingerprintSha256,
                    signer.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"签名者 {signer.KeyId} 的公钥与现有信任记录冲突");
            }

            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trusted_signers(key_id, public_key_pem, fingerprint_sha256, trusted_at)
            VALUES ($keyId, $publicKeyPem, $fingerprint, $trustedAt);
            """;
        command.Parameters.AddWithValue("$keyId", signer.KeyId);
        command.Parameters.AddWithValue("$publicKeyPem", signer.PublicKeyPem);
        command.Parameters.AddWithValue("$fingerprint", signer.FingerprintSha256);
        command.Parameters.AddWithValue("$trustedAt", signer.TrustedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TrustedPackageSigner?> FindTrustedSignerAsync(
        string keyId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_id, public_key_pem, fingerprint_sha256, trusted_at
            FROM trusted_signers
            WHERE key_id = $keyId;
            """;
        command.Parameters.AddWithValue("$keyId", keyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TrustedPackageSigner(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static async Task EnsureUploadEligibleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(snapshots);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Contains("upload_eligible"))
        {
            return;
        }

        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE snapshots ADD COLUMN upload_eligible INTEGER NOT NULL DEFAULT 1;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureRoomIdColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(snapshots);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("room_id"))
        {
            var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE snapshots ADD COLUMN room_id TEXT NULL;";
            try
            {
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode == 1
                && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // 多个后台服务可能同时首次初始化同一个索引；另一方已完成迁移即可继续。
            }
        }

        var normalize = connection.CreateCommand();
        normalize.CommandText = "UPDATE snapshots SET upload_eligible = 0 WHERE room_id IS NULL;";
        await normalize.ExecuteNonQueryAsync(cancellationToken);
    }
}
