using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Packaging;

namespace LiveStudio.Agent;

public sealed class SnapshotTransferService(
    IDeviceCredentialStore credentialStore,
    LocalSnapshotIndex snapshotIndex)
{
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
        var trustedSigner = await snapshotIndex.FindTrustedSignerAsync(
            inspection.Signer.KeyId,
            cancellationToken);
        credentialStore.TryLoad(out var credentials);
        return await SnapshotPackageReader.ReadAsync(
            record.PackagePath,
            keyId => ResolveLocalVerificationKey(keyId, credentials, trustedSigner),
            cancellationToken);
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
        var snapshotDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Snapshots");
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
                uploadEligible),
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

        if (!credentialStore.TryLoad(out var credentials)
            || !string.Equals(
                signer.KeyId,
                credentials.DeviceId.ToString("N"),
                StringComparison.OrdinalIgnoreCase))
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
        if (credentials is not null
            && string.Equals(keyId, credentials.DeviceId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(credentials.PackageSigningPrivateKeyPem);
            return CreatePublicKey(signingKey.ExportSubjectPublicKeyInfoPem());
        }

        return trustedSigner is not null
            && string.Equals(keyId, trustedSigner.KeyId, StringComparison.Ordinal)
            ? CreatePublicKey(trustedSigner.PublicKeyPem)
            : null;
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
}

public sealed class SnapshotSignerTrustRequiredException(PackageSigner signer)
    : Exception($"签名者 {signer.KeyId} 尚未受信任，公钥指纹 {signer.FingerprintSha256}")
{
    public PackageSigner Signer { get; } = signer;
}
