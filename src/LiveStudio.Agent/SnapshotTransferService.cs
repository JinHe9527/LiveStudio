using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Packaging;

namespace LiveStudio.Agent;

public sealed class SnapshotTransferService
{
    private static readonly string DefaultSnapshotDirectory = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "Snapshots"));
    private readonly IDeviceCredentialStore credentialStore;
    private readonly LocalSnapshotIndex snapshotIndex;
    private readonly string snapshotDirectory;

    public SnapshotTransferService(
        IDeviceCredentialStore credentialStore,
        LocalSnapshotIndex snapshotIndex)
        : this(credentialStore, snapshotIndex, DefaultSnapshotDirectory)
    {
    }

    internal SnapshotTransferService(
        IDeviceCredentialStore credentialStore,
        LocalSnapshotIndex snapshotIndex,
        string snapshotDirectory)
    {
        this.credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        this.snapshotIndex = snapshotIndex ?? throw new ArgumentNullException(nameof(snapshotIndex));
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        this.snapshotDirectory = Path.GetFullPath(snapshotDirectory);
    }

    public async Task<SnapshotPackage> ReadLocalAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var record = await snapshotIndex.FindAsync(snapshotId, cancellationToken)
            ?? throw new FileNotFoundException($"找不到本地存档 {snapshotId}");
        var identity = await ComputeFileIdentityAsync(record.PackagePath, cancellationToken);
        if (!string.Equals(identity.Sha256, record.Sha256, StringComparison.Ordinal)
            || identity.Length != record.Length)
        {
            throw new SnapshotPackageException("本地存档内容与索引记录不一致");
        }

        var inspection = await SnapshotPackageReader.InspectAsync(record.PackagePath, cancellationToken);
        var signerTrusted = await ResolveTrustAsync(inspection.Signer, cancellationToken);
        if (!signerTrusted)
        {
            // 旧版本在云端注册时会轮换本机签名身份，导致注册前由本机创建的存档
            // 无法再由当前凭据解析。文件已由本机索引固定 SHA-256 和长度，InspectAsync
            // 也已验证包内签名及逐文件哈希，因此可安全迁移这一个本机签名者。
            await snapshotIndex.SaveTrustedSignerAsync(
                new TrustedPackageSigner(
                    inspection.Signer.KeyId,
                    inspection.Signer.PublicKeyPem,
                    inspection.Signer.FingerprintSha256,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        var trustedSigner = await snapshotIndex.FindTrustedSignerAsync(
            inspection.Signer.KeyId,
            cancellationToken);
        credentialStore.TryLoad(out var credentials);
        var package = signerTrusted
            ? await SnapshotPackageReader.ReadAsync(
                record.PackagePath,
                keyId => ResolveLocalVerificationKey(keyId, credentials, trustedSigner),
                cancellationToken)
            : inspection.Package;
        return string.Equals(package.Snapshot.Name, record.Name, StringComparison.Ordinal)
            ? package
            : package with { Snapshot = package.Snapshot with { Name = record.Name } };
    }

    public async Task<SnapshotImportPreview> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        ValidatePackagePath(packagePath);
        var inspection = await SnapshotPackageReader.InspectAsync(packagePath, cancellationToken);
        var trusted = await ResolveTrustAsync(inspection.Signer, cancellationToken);
        return new SnapshotImportPreview(
            inspection.Package.Snapshot.Id,
            inspection.Package.Snapshot.Name,
            inspection.Package.Snapshot.CreatedAt,
            inspection.Signer.KeyId,
            inspection.Signer.FingerprintSha256,
            trusted);
    }

    public async Task<SnapshotTransferResult> ImportAsync(
        string packagePath,
        bool trustSigner,
        CancellationToken cancellationToken)
    {
        ValidatePackagePath(packagePath);
        var inspection = await SnapshotPackageReader.InspectAsync(packagePath, cancellationToken);
        var signerTrusted = await ResolveTrustAsync(inspection.Signer, cancellationToken);
        if (!signerTrusted && !trustSigner)
        {
            throw new SnapshotSignerTrustRequiredException(inspection.Signer);
        }

        if (!signerTrusted)
        {
            await snapshotIndex.SaveTrustedSignerAsync(
                new TrustedPackageSigner(
                    inspection.Signer.KeyId,
                    inspection.Signer.PublicKeyPem,
                    inspection.Signer.FingerprintSha256,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        var package = await SnapshotPackageReader.ReadAsync(
            packagePath,
            keyId => string.Equals(keyId, inspection.Signer.KeyId, StringComparison.Ordinal)
                ? CreatePublicKey(inspection.Signer.PublicKeyPem)
                : null,
            cancellationToken);
        var sourcePath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(snapshotDirectory);
        var destinationPath = Path.Combine(snapshotDirectory, $"{package.Snapshot.Id:N}.lscfg");
        var (hash, length) = await ComputeFileIdentityAsync(sourcePath, cancellationToken);
        if (File.Exists(destinationPath))
        {
            var existing = await ComputeFileIdentityAsync(destinationPath, cancellationToken);
            if (!string.Equals(existing.Sha256, hash, StringComparison.Ordinal)
                || existing.Length != length)
            {
                throw new SnapshotPackageException($"本机已存在 ID 相同但内容不同的存档 {package.Snapshot.Id}");
            }
        }
        else
        {
            await CopyAtomicallyAsync(sourcePath, destinationPath, cancellationToken);
        }

        var uploadEligible = IsUploadEligible(package.Snapshot, inspection.Signer);
        await snapshotIndex.SaveAsync(
            new LocalSnapshotRecord(
                package.Snapshot.Id,
                package.Snapshot.Name,
                destinationPath,
                hash,
                length,
                package.Snapshot.CreatedAt,
                false,
                uploadEligible,
                package.Snapshot.RoomId == Guid.Empty ? null : package.Snapshot.RoomId),
            cancellationToken);
        return new SnapshotTransferResult(package.Snapshot.Id, package.Snapshot.Name, destinationPath);
    }

    public async Task<SnapshotTransferResult> ExportAsync(
        Guid snapshotId,
        string targetPath,
        CancellationToken cancellationToken)
    {
        ValidatePackagePath(targetPath, mustExist: false);
        var snapshot = await snapshotIndex.FindAsync(snapshotId, cancellationToken)
            ?? throw new FileNotFoundException($"找不到本地存档 {snapshotId}");
        var identity = await ComputeFileIdentityAsync(snapshot.PackagePath, cancellationToken);
        if (!string.Equals(identity.Sha256, snapshot.Sha256, StringComparison.Ordinal)
            || identity.Length != snapshot.Length)
        {
            throw new SnapshotPackageException("本地存档内容与索引记录不一致，拒绝导出");
        }

        await EnsurePackageSafeToShareAsync(snapshot.PackagePath, cancellationToken);

        var destinationPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new DirectoryNotFoundException("无法确定导出目录");
        Directory.CreateDirectory(directory);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"导出文件已经存在: {destinationPath}");
        }

        await CopyAtomicallyAsync(snapshot.PackagePath, destinationPath, cancellationToken);
        return new SnapshotTransferResult(snapshot.Id, snapshot.Name, destinationPath);
    }

    public async Task<DeleteSnapshotsResult> DeleteAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var snapshot = await snapshotIndex.FindAsync(snapshotId, cancellationToken)
            ?? throw new FileNotFoundException($"找不到本地存档 {snapshotId}");
        var packagePath = ValidateManagedSnapshotPath(snapshot.PackagePath);
        var stagedFile = StageForDeletion(packagePath);
        try
        {
            var deleted = await snapshotIndex.DeleteAsync(snapshotId, cancellationToken);
            if (!deleted)
            {
                throw new IOException("存档索引发生变化，请刷新后重试");
            }
        }
        catch
        {
            RestoreStagedFile(stagedFile);
            throw;
        }

        CompleteStagedDeletion(stagedFile);
        return new DeleteSnapshotsResult(1);
    }

    public async Task<LocalSnapshotOperationResult> RenameAsync(
        Guid snapshotId,
        string name,
        CancellationToken cancellationToken)
    {
        var snapshot = await snapshotIndex.FindAsync(snapshotId, cancellationToken)
            ?? throw new FileNotFoundException($"找不到本地存档 {snapshotId}");
        var normalizedName = name.Trim();
        if (!await snapshotIndex.RenameAsync(snapshotId, normalizedName, cancellationToken))
        {
            throw new IOException("存档索引发生变化，请刷新后重试");
        }

        return new LocalSnapshotOperationResult(snapshotId, normalizedName, DateTimeOffset.UtcNow);
    }

    public async Task<LocalSnapshotOperationResult> UpdateCameraStationsAsync(
        Guid snapshotId,
        IReadOnlyList<CameraStationSnapshot> cameraStations,
        CancellationToken cancellationToken)
    {
        var normalizedStations = SnapshotCaptureService.NormalizeCameraStations(cameraStations);
        var record = await snapshotIndex.FindAsync(snapshotId, cancellationToken)
            ?? throw new FileNotFoundException($"找不到本地存档 {snapshotId}");
        if (record.Uploaded)
        {
            throw new InvalidOperationException("这份存档已同步到云端；请点“保存当前画面”生成包含新相机参数的新存档");
        }

        var package = await ReadLocalAsync(snapshotId, cancellationToken);
        var packagePath = ValidateManagedSnapshotPath(record.PackagePath);
        var credentials = credentialStore.Load();
        var directory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("无法确定本机存档目录");
        var replacementPath = Path.Combine(directory, $"{snapshotId:N}.camera-{Guid.NewGuid():N}.lscfg");
        var backupPath = $"{packagePath}.camera-backup-{Guid.NewGuid():N}";
        var files = package.Files.Values
            .Where(file => !string.Equals(file.Path, "parameters.json", StringComparison.Ordinal))
            .ToArray();
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(credentials.PackageSigningPrivateKeyPem);
        await SnapshotPackageWriter.WriteAsync(
            replacementPath,
            package.Snapshot with { CameraStations = normalizedStations },
            files,
            signingKey,
            credentials.DeviceId.ToString("N"),
            cancellationToken);

        File.Move(packagePath, backupPath);
        try
        {
            File.Move(replacementPath, packagePath);
            var identity = await ComputeFileIdentityAsync(packagePath, cancellationToken);
            await snapshotIndex.SaveAsync(
                record with
                {
                    Sha256 = identity.Sha256,
                    Length = identity.Length,
                    Uploaded = false
                },
                cancellationToken);
        }
        catch
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            if (File.Exists(backupPath))
            {
                File.Move(backupPath, packagePath);
            }

            throw;
        }
        finally
        {
            if (File.Exists(replacementPath))
            {
                File.Delete(replacementPath);
            }
        }

        try
        {
            File.Delete(backupPath);
        }
        catch (IOException)
        {
            // The committed package and index already agree. A stale backup is safer than
            // rolling back after commit and making the index point at the wrong hash.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new LocalSnapshotOperationResult(snapshotId, record.Name, DateTimeOffset.UtcNow);
    }

    public async Task<DeleteSnapshotsResult> DeleteAllAsync(CancellationToken cancellationToken)
    {
        var snapshots = await snapshotIndex.GetAllAsync(cancellationToken);
        var packagePaths = snapshots
            .Select(snapshot => ValidateManagedSnapshotPath(snapshot.PackagePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var stagedFiles = new List<StagedDeletion>(packagePaths.Length);
        try
        {
            foreach (var packagePath in packagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedFile = StageForDeletion(packagePath);
                if (stagedFile is not null)
                {
                    stagedFiles.Add(stagedFile);
                }
            }
        }
        catch
        {
            foreach (var stagedFile in stagedFiles.AsEnumerable().Reverse())
            {
                RestoreStagedFile(stagedFile);
            }

            throw;
        }

        int deletedCount;
        try
        {
            deletedCount = await snapshotIndex.DeleteAllAsync(cancellationToken);
        }
        catch
        {
            foreach (var stagedFile in stagedFiles.AsEnumerable().Reverse())
            {
                RestoreStagedFile(stagedFile);
            }

            throw;
        }

        foreach (var stagedFile in stagedFiles)
        {
            CompleteStagedDeletion(stagedFile);
        }

        return new DeleteSnapshotsResult(deletedCount);
    }

    public static async Task PublishAsync(
        LocalSnapshotRecord snapshot,
        string sharedDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedDirectory);
        if (!Directory.Exists(sharedDirectory))
        {
            throw new DirectoryNotFoundException($"局域网共享目录不可用: {sharedDirectory}");
        }

        var sourceIdentity = await ComputeFileIdentityAsync(snapshot.PackagePath, cancellationToken);
        if (!string.Equals(sourceIdentity.Sha256, snapshot.Sha256, StringComparison.Ordinal)
            || sourceIdentity.Length != snapshot.Length)
        {
            throw new SnapshotPackageException($"本机存档 {snapshot.Id} 内容与索引记录不一致");
        }

        await EnsurePackageSafeToShareAsync(snapshot.PackagePath, cancellationToken);

        var destinationPath = Path.Combine(sharedDirectory, $"{snapshot.Id:N}.lscfg");
        if (File.Exists(destinationPath))
        {
            var destinationIdentity = await ComputeFileIdentityAsync(destinationPath, cancellationToken);
            if (!string.Equals(destinationIdentity.Sha256, sourceIdentity.Sha256, StringComparison.Ordinal)
                || destinationIdentity.Length != sourceIdentity.Length)
            {
                throw new SnapshotPackageException($"共享目录存在 ID 相同但内容不同的存档 {snapshot.Id}");
            }

            return;
        }

        await CopyAtomicallyAsync(snapshot.PackagePath, destinationPath, cancellationToken);
    }

    private static async Task EnsurePackageSafeToShareAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await SnapshotPackageReader.InspectAsync(packagePath, cancellationToken);
        }
        catch (SnapshotSensitiveDataException exception)
        {
            throw new SnapshotSensitiveDataException(
                "该旧存档包含敏感配置，已阻止导出或同步。请重新保存当前画面后再操作。",
                exception);
        }
    }

    private async Task<bool> ResolveTrustAsync(PackageSigner signer, CancellationToken cancellationToken)
    {
        var trusted = await snapshotIndex.FindTrustedSignerAsync(signer.KeyId, cancellationToken);
        if (trusted is not null)
        {
            if (!string.Equals(
                    trusted.FingerprintSha256,
                    signer.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SnapshotPackageException($"签名者 {signer.KeyId} 的公钥与现有信任记录冲突");
            }

            return true;
        }

        if (!credentialStore.TryLoad(out var credentials))
        {
            return false;
        }

        using var ownKey = ECDsa.Create();
        ownKey.ImportFromPem(credentials.PackageSigningPrivateKeyPem);
        var ownFingerprint = Convert.ToHexStringLower(SHA256.HashData(ownKey.ExportSubjectPublicKeyInfo()));
        return string.Equals(ownFingerprint, signer.FingerprintSha256, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUploadEligible(CombinedSnapshot snapshot, PackageSigner signer)
    {
        if (!credentialStore.TryLoad(out var credentials) || !credentials.IsCloudEnrolled)
        {
            return false;
        }

        return snapshot.OrganizationId == credentials.OrganizationId
            && snapshot.RoomId == credentials.RoomId
            && string.Equals(
                signer.KeyId,
                credentials.DeviceId.ToString("N"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static ECDsa CreatePublicKey(string publicKeyPem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key;
    }

    private static ECDsa? ResolveLocalVerificationKey(
        string keyId,
        DeviceCredentials? credentials,
        TrustedPackageSigner? trustedSigner)
    {
        if (trustedSigner is not null
            && string.Equals(keyId, trustedSigner.KeyId, StringComparison.Ordinal))
        {
            return CreatePublicKey(trustedSigner.PublicKeyPem);
        }

        // 云端注册会为这台电脑分配新的 DeviceId，但本机签名私钥保持不变。
        // 旧存档的 KeyId 因此可能不同；返回当前本机公钥后，Reader 仍会逐字节核对
        // 包内公钥，非本机签名的文件不会因此被信任。
        if (credentials is not null)
        {
            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(credentials.PackageSigningPrivateKeyPem);
            return CreatePublicKey(signingKey.ExportSubjectPublicKeyInfoPem());
        }

        return null;
    }

    private static void ValidatePackagePath(string packagePath, bool mustExist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!string.Equals(Path.GetExtension(packagePath), ".lscfg", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotPackageException("存档文件必须使用 .lscfg 扩展名");
        }

        if (mustExist && !File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到存档文件", packagePath);
        }
    }

    private string ValidateManagedSnapshotPath(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var directoryPrefix = snapshotDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? snapshotDirectory
            : snapshotDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".lscfg", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotPackageException("存档文件不在 LiveStudio 受管目录中，拒绝删除");
        }

        return fullPath;
    }

    private static StagedDeletion? StageForDeletion(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            return null;
        }

        var stagedPath = $"{packagePath}.deleting-{Guid.NewGuid():N}";
        File.Move(packagePath, stagedPath, false);
        return new StagedDeletion(packagePath, stagedPath);
    }

    private static void RestoreStagedFile(StagedDeletion? stagedFile)
    {
        if (stagedFile is null || !File.Exists(stagedFile.StagedPath))
        {
            return;
        }

        File.Move(stagedFile.StagedPath, stagedFile.OriginalPath, false);
    }

    private static void CompleteStagedDeletion(StagedDeletion? stagedFile)
    {
        if (stagedFile is null)
        {
            return;
        }

        try
        {
            File.Delete(stagedFile.StagedPath);
        }
        catch (IOException)
        {
            // 索引已经提交删除；带专用后缀的临时文件不会重新出现在 UI 中。
        }
        catch (UnauthorizedAccessException)
        {
            // 下次维护可安全清理该临时文件，不能把已提交的删除伪装为失败。
        }
    }

    private static async Task<(string Sha256, long Length)> ComputeFileIdentityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return (hash, stream.Length);
    }

    private static async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.partial-{Guid.NewGuid():N}";
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131_072,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

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

    private sealed record StagedDeletion(string OriginalPath, string StagedPath);
}

public sealed class SnapshotSignerTrustRequiredException(PackageSigner signer)
    : Exception($"签名者 {signer.KeyId} 尚未受信任，公钥指纹 {signer.FingerprintSha256}")
{
    public PackageSigner Signer { get; } = signer;
}
