using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.Obs;

public static class ObsRecovery
{
    public static Task RecoverPendingAsync(ObsAdapter adapter, CancellationToken cancellationToken) =>
        ObsTransactionJournal.RecoverPendingAsync(adapter, cancellationToken);
}

internal sealed class ObsTransactionJournal
{
    private const string ManifestFileName = "transaction.json";
    private const string SnapshotFileName = "rollback.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string transactionDirectory;
    private readonly ObsTransactionManifest manifest;

    private ObsTransactionJournal(string transactionDirectory, ObsTransactionManifest manifest)
    {
        this.transactionDirectory = transactionDirectory;
        this.manifest = manifest;
    }

    public string AssetDirectory => Path.Combine(transactionDirectory, "assets");

    public IReadOnlyList<string> CreatedSourceNames => manifest.CreatedSourceNames;

    public static async Task<ObsTransactionJournal> CreateAsync(
        Guid transactionId,
        ApplicationSnapshot rollbackSnapshot,
        bool wasRunning,
        IReadOnlyList<string> createdSourceNames,
        CancellationToken cancellationToken)
    {
        var transactionDirectory = Path.Combine(RootDirectory, transactionId.ToString("N"));
        Directory.CreateDirectory(transactionDirectory);
        var snapshotContent = JsonSerializer.SerializeToUtf8Bytes(rollbackSnapshot, JsonOptions);
        var journal = new ObsTransactionJournal(
            transactionDirectory,
            new ObsTransactionManifest(
                transactionId,
                DateTimeOffset.UtcNow,
                wasRunning,
                SnapshotFileName,
                createdSourceNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Convert.ToHexStringLower(SHA256.HashData(snapshotContent)),
                snapshotContent.LongLength));
        try
        {
            await journal.CopyAssetsAsync(rollbackSnapshot, cancellationToken);
            await WriteDurableAsync(
                Path.Combine(transactionDirectory, SnapshotFileName),
                snapshotContent,
                cancellationToken);
            await journal.WriteManifestAsync(cancellationToken);
            return journal;
        }
        catch
        {
            if (Directory.Exists(transactionDirectory))
            {
                Directory.Delete(transactionDirectory, recursive: true);
            }

            throw;
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(transactionDirectory))
        {
            Directory.Delete(transactionDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static async Task RecoverPendingAsync(
        ObsAdapter adapter,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(directory, ManifestFileName)))
            {
                // The directory is created before BeginRestoreAsync returns. Without a manifest
                // no OBS write could have started, so an interrupted journal initialization is
                // safe to discard.
                Directory.Delete(directory, recursive: true);
                continue;
            }

            var manifest = await ReadManifestAsync(directory, cancellationToken);
            if (await RestoreTransactionJournal.IsCommittedAsync(
                    manifest.TransactionId,
                    cancellationToken))
            {
                Directory.Delete(directory, recursive: true);
                continue;
            }

            var snapshotPath = GetContainedPath(directory, manifest.SnapshotFileName);
            if (!File.Exists(snapshotPath))
            {
                throw new InvalidOperationException($"OBS 回滚快照不存在: {snapshotPath}");
            }

            var snapshotContent = await File.ReadAllBytesAsync(snapshotPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(manifest.SnapshotSha256)
                && (snapshotContent.LongLength != manifest.SnapshotLength
                    || !string.Equals(
                        Convert.ToHexStringLower(SHA256.HashData(snapshotContent)),
                        manifest.SnapshotSha256,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"OBS 回滚快照校验失败: {snapshotPath}");
            }

            var rollbackSnapshot = JsonSerializer.Deserialize<ApplicationSnapshot>(
                snapshotContent,
                JsonOptions) ?? throw new InvalidOperationException($"无法解析 OBS 回滚快照 {snapshotPath}");
            if (rollbackSnapshot.Kind != ApplicationKind.Obs)
            {
                throw new InvalidOperationException($"OBS 回滚快照应用类型无效: {snapshotPath}");
            }

            var assetDirectory = Path.Combine(directory, "assets");
            await ValidateAssetsAsync(rollbackSnapshot, assetDirectory, cancellationToken);

            var running = ObsProcessController.FindRunning();
            if (running is null)
            {
                await ObsProcessController.StartAsync(cancellationToken);
            }

            await adapter.WaitUntilConnectedForRecoveryAsync(cancellationToken);
            await adapter.RemoveInputsAsync(manifest.CreatedSourceNames, cancellationToken);
            var rollbackMappings = CreateRollbackMappings(rollbackSnapshot);
            var unusedCreatedSources = new List<string>();
            await adapter.ApplySnapshotAsync(
                rollbackSnapshot,
                rollbackMappings,
                assetDirectory,
                unusedCreatedSources,
                cancellationToken);
            if (!manifest.WasRunning
                && ObsProcessController.FindRunning() is { } temporary)
            {
                await ObsProcessController.StopAsync(temporary.ProcessId, cancellationToken);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    internal static IReadOnlyList<DeviceMapping> CreateRollbackMappings(ApplicationSnapshot snapshot) =>
        snapshot.Sources.Select(source => new DeviceMapping(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            source.LogicalId,
            ApplicationKind.Obs,
            source.Settings.TryGetValue("video_device_id", out var deviceId)
                ? deviceId.GetString() ?? string.Empty
                : string.Empty,
            source.Name,
            string.Empty,
            false)).ToArray();

    private async Task CopyAssetsAsync(
        ApplicationSnapshot rollbackSnapshot,
        CancellationToken cancellationToken)
    {
        foreach (var asset in rollbackSnapshot.Sources
                     .SelectMany(source => source.Filters)
                     .SelectMany(filter => filter.Assets)
                     .DistinctBy(
                         asset => $"{asset.BlobSha256}\0{asset.OriginalFileName}",
                         StringComparer.Ordinal))
        {
            if (!File.Exists(asset.SourcePath))
            {
                throw new FileNotFoundException($"OBS 回滚素材不存在: {asset.OriginalFileName}", asset.SourcePath);
            }

            var destinationDirectory = Path.Combine(AssetDirectory, asset.BlobSha256);
            Directory.CreateDirectory(destinationDirectory);
            var destinationPath = Path.Combine(destinationDirectory, asset.OriginalFileName);
            await using var source = File.OpenRead(asset.SourcePath);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
            if (!string.Equals(hash, asset.BlobSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"OBS 回滚素材哈希已变化: {asset.OriginalFileName}");
            }

            source.Position = 0;
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }
    }

    private static async Task ValidateAssetsAsync(
        ApplicationSnapshot rollbackSnapshot,
        string assetDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var asset in rollbackSnapshot.Sources
                     .SelectMany(source => source.Filters)
                     .SelectMany(filter => filter.Assets)
                     .DistinctBy(
                         asset => $"{asset.BlobSha256}\0{asset.OriginalFileName}",
                         StringComparer.Ordinal))
        {
            if (asset.BlobSha256.Length != 64
                || asset.BlobSha256.Any(character => !Uri.IsHexDigit(character))
                || string.IsNullOrWhiteSpace(asset.OriginalFileName)
                || !string.Equals(
                    Path.GetFileName(asset.OriginalFileName),
                    asset.OriginalFileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OBS 回滚素材元数据无效");
            }

            var path = GetContainedPath(
                assetDirectory,
                Path.Combine(asset.BlobSha256, asset.OriginalFileName));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"OBS 回滚素材不存在: {asset.OriginalFileName}", path);
            }

            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, asset.BlobSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"OBS 回滚素材校验失败: {asset.OriginalFileName}");
            }
        }
    }

    private Task WriteManifestAsync(CancellationToken cancellationToken) => WriteDurableAsync(
        Path.Combine(transactionDirectory, ManifestFileName),
        JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
        cancellationToken);

    private static async Task<ObsTransactionManifest> ReadManifestAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"OBS 事务缺少清单: {manifestPath}");
        }

        var manifest = JsonSerializer.Deserialize<ObsTransactionManifest>(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken),
            JsonOptions) ?? throw new InvalidOperationException($"无法解析 OBS 事务 {manifestPath}");
        if (manifest.TransactionId == Guid.Empty
            || !string.Equals(
                Path.GetFullPath(directory),
                Path.GetFullPath(Path.Combine(RootDirectory, manifest.TransactionId.ToString("N"))),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"OBS 事务标识或目录无效: {manifestPath}");
        }

        _ = GetContainedPath(directory, manifest.SnapshotFileName);
        return manifest;
    }

    private static async Task WriteDurableAsync(
        string destinationPath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetContainedPath(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("OBS 事务文件名无效");
        }

        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                   + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OBS 事务文件路径超出事务目录");
        }

        return path;
    }

    private static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "Transactions",
        "Obs");

    private sealed record ObsTransactionManifest(
        Guid TransactionId,
        DateTimeOffset CreatedAt,
        bool WasRunning,
        string SnapshotFileName,
        IReadOnlyList<string> CreatedSourceNames,
        string? SnapshotSha256 = null,
        long SnapshotLength = 0);
}
