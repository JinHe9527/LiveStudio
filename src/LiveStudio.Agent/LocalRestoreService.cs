using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Core;
using LiveStudio.Packaging;

namespace LiveStudio.Agent;

public sealed class LocalRestoreService(
    IDeviceCredentialStore credentialStore,
    LocalSnapshotIndex snapshotIndex,
    SnapshotTransferService transferService,
    RestoreCoordinator restoreCoordinator)
{
    public async Task<RestoreExecutionResult> RestoreAsync(
        Guid snapshotId,
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
            reportProgress,
            cancellationToken);
    }

    private async Task<RestoreExecutionResult> ExecutePackageAsync(
        SnapshotPackage package,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var assetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Assets");
        ValidateAssetEntries(package);
        return await restoreCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            package.Snapshot,
            mappings,
            isUnattended,
            assetDirectory,
            token => MaterializeAssetsAsync(package, assetDirectory, token),
            reportProgress,
            cancellationToken);
    }

    private static void ValidateAssetEntries(SnapshotPackage package)
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
                throw new SnapshotPackageException($"滤镜素材元数据不一致: {asset.OriginalFileName}");
            }
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
        foreach (var asset in package.Snapshot.Assets)
        {
            if (!package.Files.TryGetValue(asset.PackagePath, out var file))
            {
                throw new SnapshotPackageException($"存档缺少滤镜素材 {asset.PackagePath}");
            }

            var fileName = Path.GetFileName(asset.OriginalFileName);
            if (string.IsNullOrWhiteSpace(fileName)
                || !string.Equals(fileName, asset.OriginalFileName, StringComparison.Ordinal))
            {
                throw new SnapshotPackageException($"滤镜素材文件名无效: {asset.OriginalFileName}");
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
}
