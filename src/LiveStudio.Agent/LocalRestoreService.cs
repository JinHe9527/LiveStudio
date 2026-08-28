using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Core;
using LiveStudio.Packaging;

namespace LiveStudio.Agent;

public sealed class LocalRestoreService(
    IDeviceCredentialStore credentialStore,
    LocalSnapshotIndex snapshotIndex,
    SnapshotTransferService transferService,
    RestoreCoordinator restoreCoordinator,
    SnapshotCaptureService snapshotCaptureService)
{
    public async Task<RestoreExecutionResult> RestoreAsync(
        Guid snapshotId,
        IReadOnlyList<CameraStationSnapshot>? currentCameraStations,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var credentials = credentialStore.Load();
        var package = await transferService.ReadLocalAsync(snapshotId, cancellationToken);
        var mappings = await snapshotIndex.GetMappingsAsync(credentials.DeviceId, cancellationToken);
        return await ExecutePackageAsync(
            package,
            mappings,
            false,
            currentCameraStations,
            reportProgress,
            cancellationToken);
    }

    public async Task<RestoreExecutionResult> RestoreDownloadedAsync(
        DownloadedSnapshotPackage download,
        IReadOnlyList<DeviceMapping> mappings,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        using var verificationKey = ECDsa.Create();
        verificationKey.ImportFromPem(download.SigningPublicKeyPem);
        var package = await SnapshotPackageReader.ReadAsync(
            download.PackagePath,
            keyId => string.Equals(keyId, download.SigningKeyId, StringComparison.OrdinalIgnoreCase)
                ? CreateVerificationKey(verificationKey)
                : null,
            cancellationToken);
        return await ExecutePackageAsync(
            package,
            mappings,
            true,
            null,
            reportProgress,
            cancellationToken);
    }

    private async Task<RestoreExecutionResult> ExecutePackageAsync(
        SnapshotPackage package,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        IReadOnlyList<CameraStationSnapshot>? currentCameraStations,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var assetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Assets");
        ValidateAssetEntries(package);
        EnsureAssetCapacity(package, assetDirectory);
        return await restoreCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            package.Snapshot,
            mappings,
            isUnattended,
            assetDirectory,
            token => MaterializeAssetsAsync(package, assetDirectory, token),
            reportProgress,
            token => snapshotCaptureService.CaptureForRestoreBackupAsync(
                $"恢复前自动备份 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                currentCameraStations,
                token),
            cancellationToken);
    }

    internal static void ValidateAssetEntries(SnapshotPackage package)
    {
        foreach (var asset in package.Snapshot.Assets)
        {
            if (!package.Files.TryGetValue(asset.PackagePath, out var file))
            {
                throw new SnapshotPackageException($"存档缺少滤镜素材 {asset.PackagePath}");
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(file.Content.Span));
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)
                || file.Content.Length != asset.Length
                || !string.Equals(file.MediaType, asset.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                throw new SnapshotPackageException($"滤镜素材元数据不一致: {asset.Sha256}");
            }
        }

        var blobs = package.Snapshot.Assets.ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var bindings = SnapshotAssetBindings.Collect(package.Snapshot.Applications);
        var missingBinding = bindings.FirstOrDefault(binding => !blobs.ContainsKey(binding.BlobSha256));
        if (missingBinding is not null)
        {
            throw new SnapshotPackageException($"滤镜素材引用缺少内容 Blob: {missingBinding.OriginalFileName}");
        }

        var invalidLength = bindings.FirstOrDefault(binding =>
            binding.Length <= 0 || blobs.TryGetValue(binding.BlobSha256, out var blob) && binding.Length != blob.Length);
        if (invalidLength is not null)
        {
            throw new SnapshotPackageException($"滤镜素材长度不一致: {invalidLength.OriginalFileName}");
        }

        var invalidFileName = bindings.FirstOrDefault(binding =>
            string.IsNullOrWhiteSpace(binding.OriginalFileName)
            || !string.Equals(
                Path.GetFileName(binding.OriginalFileName),
                binding.OriginalFileName,
                StringComparison.Ordinal));
        if (invalidFileName is not null)
        {
            throw new SnapshotPackageException($"滤镜素材文件名无效: {invalidFileName.OriginalFileName}");
        }
    }

    private static ECDsa CreateVerificationKey(ECDsa sourceKey)
    {
        var verificationKey = ECDsa.Create();
        verificationKey.ImportSubjectPublicKeyInfo(sourceKey.ExportSubjectPublicKeyInfo(), out _);
        return verificationKey;
    }

    private static async Task MaterializeAssetsAsync(
        SnapshotPackage package,
        string assetDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(assetDirectory);
        var blobs = package.Snapshot.Assets.ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var bindings = SnapshotAssetBindings.Collect(package.Snapshot.Applications)
            .DistinctBy(binding => $"{binding.BlobSha256}\0{binding.OriginalFileName}", StringComparer.Ordinal)
            .ToArray();
        foreach (var binding in bindings)
        {
            var asset = blobs[binding.BlobSha256];
            if (!package.Files.TryGetValue(asset.PackagePath, out var file))
            {
                throw new SnapshotPackageException($"存档缺少滤镜素材 {asset.PackagePath}");
            }

            var fileName = Path.GetFileName(binding.OriginalFileName);
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(fileName, binding.OriginalFileName, StringComparison.Ordinal))
            {
                throw new SnapshotPackageException($"滤镜素材文件名无效: {binding.OriginalFileName}");
            }

            var destinationDirectory = Path.Combine(assetDirectory, asset.Sha256.ToLowerInvariant());
            Directory.CreateDirectory(destinationDirectory);
            var destinationPath = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destinationPath))
            {
                await using var existing = File.OpenRead(destinationPath);
                var existingHash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(existing, cancellationToken));
                if (!string.Equals(existingHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SnapshotPackageException($"本机素材目录存在哈希冲突: {fileName}");
                }

                continue;
            }

            var temporaryPath = $"{destinationPath}.partial-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, file.Content.ToArray(), cancellationToken);
                File.Move(temporaryPath, destinationPath, false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static void EnsureAssetCapacity(SnapshotPackage package, string assetDirectory)
    {
        var fullPath = Path.GetFullPath(assetDirectory);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"无法确定素材目录所在磁盘: {fullPath}");
        var blobs = package.Snapshot.Assets.ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var requiredBytes = SnapshotAssetBindings.Collect(package.Snapshot.Applications)
            .DistinctBy(binding => $"{binding.BlobSha256}\0{binding.OriginalFileName}", StringComparer.Ordinal)
            .Where(binding => !File.Exists(Path.Combine(
                fullPath,
                binding.BlobSha256.ToLowerInvariant(),
                binding.OriginalFileName)))
            .Sum(binding => blobs[binding.BlobSha256].Length);
        var availableBytes = new DriveInfo(root).AvailableFreeSpace;
        if (availableBytes < requiredBytes + 64L * 1024 * 1024)
        {
            throw new IOException(
                $"素材目录磁盘空间不足，需要 {requiredBytes} 字节并保留 64 MiB，当前可用 {availableBytes} 字节");
        }
    }
}
