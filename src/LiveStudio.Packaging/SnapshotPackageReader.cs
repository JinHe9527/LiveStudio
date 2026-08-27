using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

public static class SnapshotPackageReader
{
    private static readonly HashSet<int> SupportedSchemaVersions = [2, 3];
    private const long MaximumManifestLength = 4L * 1024 * 1024;
    private const long MaximumSignatureLength = 256 * 1024;
    private const long MaximumEntryLength = 512L * 1024 * 1024;
    private const long MaximumPackageLength = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<SnapshotPackage> ReadAsync(
        string packagePath,
        Func<string, ECDsa?> resolveVerificationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveVerificationKey);
        var result = await ReadCoreAsync(
            packagePath,
            resolveVerificationKey,
            allowEmbeddedKey: false,
            cancellationToken);
        return result.Package;
    }

    public static Task<SnapshotPackageInspection> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken) => ReadCoreAsync(
            packagePath,
            _ => null,
            allowEmbeddedKey: true,
            cancellationToken);

    private static async Task<SnapshotPackageInspection> ReadCoreAsync(
        string packagePath,
        Func<string, ECDsa?> resolveVerificationKey,
        bool allowEmbeddedKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        EnsureSafeEntries(archive);

        var manifestBytes = await ReadMetadataEntryAsync(
            archive,
            "manifest.json",
            MaximumManifestLength,
            cancellationToken);
        var signatureBytes = await ReadMetadataEntryAsync(
            archive,
            "signature.json",
            MaximumSignatureLength,
            cancellationToken);
        var manifest = Deserialize<SnapshotPackageManifest>(manifestBytes, "manifest.json");
        var signature = Deserialize<PackageSignature>(signatureBytes, "signature.json");
        ValidateSignatureMetadata(signature);
        if (!SupportedSchemaVersions.Contains(manifest.SchemaVersion))
        {
            throw new SnapshotPackageException($"不支持存档 schema 版本 {manifest.SchemaVersion}");
        }

        using var embeddedKey = CreateEmbeddedKey(signature.PublicKeyPem);
        var publicKey = embeddedKey.ExportSubjectPublicKeyInfo();
        var signer = new PackageSigner(
            signature.KeyId,
            embeddedKey.ExportSubjectPublicKeyInfoPem(),
            Convert.ToHexStringLower(SHA256.HashData(publicKey)));
        using var trustedKey = resolveVerificationKey(signature.KeyId);
        if (!allowEmbeddedKey && trustedKey is null)
        {
            throw new SnapshotPackageException($"签名者尚未受信任: {signature.KeyId}");
        }

        if (trustedKey is not null
            && !CryptographicOperations.FixedTimeEquals(
                publicKey,
                trustedKey.ExportSubjectPublicKeyInfo()))
        {
            throw new SnapshotPackageException($"签名者公钥与受信任记录不一致: {signature.KeyId}");
        }

        var verificationKey = trustedKey ?? embeddedKey;
        byte[] signatureContent;
        try
        {
            signatureContent = Convert.FromBase64String(signature.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new SnapshotPackageException("存档签名编码无效", exception);
        }

        if (!verificationKey.VerifyData(
                manifestBytes.Span,
                signatureContent,
                HashAlgorithmName.SHA256))
        {
            throw new SnapshotPackageException("存档签名无效");
        }

        var expectedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var actualPaths = archive.Entries
            .Select(entry => entry.FullName)
            .Where(path => path is not "manifest.json" and not "signature.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedPaths.SetEquals(actualPaths))
        {
            throw new SnapshotPackageException("存档文件清单与实际内容不一致");
        }

        var files = new Dictionary<string, PackageFile>(StringComparer.Ordinal);
        foreach (var expected in manifest.Files)
        {
            var content = await ReadEntryAsync(archive, expected.Path, cancellationToken);
            if (content.Length != expected.Length)
            {
                throw new SnapshotPackageException($"文件长度不一致: {expected.Path}");
            }

            var actualHash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
            if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
            {
                throw new SnapshotPackageException($"文件哈希不一致: {expected.Path}");
            }

            files.Add(expected.Path, new PackageFile(expected.Path, expected.MediaType, content));
        }

        if (!files.TryGetValue("parameters.json", out var parameters))
        {
            throw new SnapshotPackageException("存档缺少 parameters.json");
        }

        EnsureNoSensitiveData(files.Values);
        var snapshot = Deserialize<CombinedSnapshot>(parameters.Content, "parameters.json");
        if (snapshot.Id != manifest.SnapshotId
            || snapshot.OrganizationId != manifest.OrganizationId
            || snapshot.RoomId != manifest.RoomId
            || snapshot.SchemaVersion != manifest.SchemaVersion
            || !string.Equals(snapshot.Name, manifest.Name, StringComparison.Ordinal)
            || snapshot.CreatedAt != manifest.CreatedAt)
        {
            throw new SnapshotPackageException("存档元数据与参数内容不一致");
        }

        EnsureManifestMatchesParameters(manifest, snapshot);
        EnsureCameraReferenceImagesMatchFiles(snapshot, files);
        snapshot = NormalizeLegacySnapshot(snapshot);

        return new SnapshotPackageInspection(
            new SnapshotPackage(manifest, snapshot, files),
            signer);
    }

    private static CombinedSnapshot NormalizeLegacySnapshot(CombinedSnapshot snapshot)
    {
        if (snapshot.SchemaVersion >= 3)
        {
            return snapshot;
        }

        return snapshot with
        {
            Applications = snapshot.Applications.Select(application => application with
            {
                Compatibility = CompatibilityLevel.Unsupported,
                FieldCoverage = application.FieldCoverage.Select(field => field with
                {
                    Writable = false,
                    Verification = "LegacySchemaReadOnly",
                    EvidenceStatus = FieldEvidenceStatus.Unknown
                }).ToArray(),
                ConfigurationTree = null,
                FilterChains = null,
                CaptureConsistency = null
            }).ToArray()
        };
    }

    private static void EnsureManifestMatchesParameters(
        SnapshotPackageManifest manifest,
        CombinedSnapshot snapshot)
    {
        var applications = snapshot.Applications.ToDictionary(application => application.Kind);
        if (applications.Count != manifest.Applications.Count)
        {
            throw new SnapshotPackageException("存档适配器清单与参数内容不一致");
        }

        foreach (var expected in manifest.Applications)
        {
            if (!applications.TryGetValue(expected.Application, out var actual)
                || !string.Equals(expected.AdapterId, actual.AdapterId, StringComparison.Ordinal)
                || !string.Equals(
                    expected.AdapterDefinitionSha256,
                    actual.AdapterDefinitionSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expected.StructureFingerprint,
                    actual.StructureFingerprint,
                    StringComparison.Ordinal)
                || expected.Compatibility != actual.Compatibility
                || expected.WasRunning != actual.WasRunning
                || !FieldCoverageMatches(expected, actual))
            {
                throw new SnapshotPackageException("存档适配器清单与参数内容不一致");
            }

            if (actual.Compatibility == CompatibilityLevel.Verified
                && (actual.AdapterDefinitionSha256.Length != 64
                    || !actual.AdapterDefinitionSha256.All(Uri.IsHexDigit)))
            {
                throw new SnapshotPackageException($"{actual.Kind} 缺少有效的适配定义哈希");
            }
        }

        var actualBindings = SnapshotAssetBindings.Collect(snapshot.Applications)
            .ToDictionary(binding => binding.Id);
        if (actualBindings.Count != manifest.AssetBindings.Count
            || manifest.AssetBindings.Any(expected =>
                !actualBindings.TryGetValue(expected.Id, out var actual)
                || !string.Equals(expected.BlobSha256, actual.BlobSha256, StringComparison.Ordinal)
                || !string.Equals(expected.OriginalFileName, actual.OriginalFileName, StringComparison.Ordinal)
                || !string.Equals(expected.SourcePath, actual.SourcePath, StringComparison.Ordinal)
                || !string.Equals(expected.ReferencePath, actual.ReferencePath, StringComparison.Ordinal)
                || expected.Length != actual.Length))
        {
            throw new SnapshotPackageException("存档素材 Binding 清单与参数内容不一致");
        }

        var manifestStations = manifest.CameraStations ?? [];
        var snapshotStations = snapshot.CameraStations ?? [];
        if (!manifestStations.SequenceEqual(snapshotStations))
        {
            throw new SnapshotPackageException("存档相机参数清单与参数内容不一致");
        }
    }

    private static bool FieldCoverageMatches(
        SnapshotApplicationManifest expected,
        ApplicationSnapshot actual)
    {
        var actualPaths = actual.FieldCoverage.Select(field => field.NativePath).ToArray();
        if (!string.IsNullOrWhiteSpace(expected.FieldCoverageSha256))
        {
            return expected.FieldCoverageCount == actualPaths.Length
                   && string.Equals(
                       expected.FieldCoverageSha256,
                       SnapshotManifestIntegrity.HashFieldCoverage(actualPaths),
                       StringComparison.Ordinal);
        }

        return expected.FieldCoverage.SequenceEqual(actualPaths, StringComparer.Ordinal);
    }

    private static void EnsureCameraReferenceImagesMatchFiles(
        CombinedSnapshot snapshot,
        IReadOnlyDictionary<string, PackageFile> files)
    {
        var referencedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in snapshot.CameraStations ?? [])
        {
            if (station.ReferenceImage is not { } reference)
            {
                continue;
            }

            if (!referencedPaths.Add(reference.PackagePath)
                || !files.TryGetValue(reference.PackagePath, out var file)
                || file.Content.Length != reference.Length
                || !string.Equals(file.MediaType, reference.MediaType, StringComparison.Ordinal)
                || !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)),
                    reference.Sha256,
                    StringComparison.Ordinal))
            {
                throw new SnapshotPackageException($"{station.Slot + 1} 号机位参考图与存档文件不一致");
            }

            ValidatedCameraReferenceImage validated;
            try
            {
                validated = CameraReferenceImageFile.Validate(file.Content);
            }
            catch (InvalidDataException exception)
            {
                throw new SnapshotPackageException($"{station.Slot + 1} 号机位参考图无效", exception);
            }

            if (validated.PixelWidth != reference.PixelWidth
                || validated.PixelHeight != reference.PixelHeight
                || !string.Equals(validated.MediaType, reference.MediaType, StringComparison.Ordinal))
            {
                throw new SnapshotPackageException($"{station.Slot + 1} 号机位参考图尺寸或格式不一致");
            }
        }

        var unreferencedImage = files.Keys.FirstOrDefault(path =>
            path.StartsWith("camera-images/", StringComparison.Ordinal)
            && !referencedPaths.Contains(path));
        if (unreferencedImage is not null)
        {
            throw new SnapshotPackageException($"存档包含未绑定的相机参考图: {unreferencedImage}");
        }
    }

    private static void ValidateSignatureMetadata(PackageSignature signature)
    {
        if (!string.Equals(signature.Algorithm, "ECDSA-P256-SHA256", StringComparison.Ordinal))
        {
            throw new SnapshotPackageException($"不支持的签名算法: {signature.Algorithm}");
        }

        if (string.IsNullOrWhiteSpace(signature.KeyId)
            || signature.KeyId.Length > 128
            || string.IsNullOrWhiteSpace(signature.PublicKeyPem)
            || signature.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new SnapshotPackageException("存档签名者信息无效");
        }
    }

    private static ECDsa CreateEmbeddedKey(string publicKeyPem)
    {
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256)
            {
                key.Dispose();
                throw new SnapshotPackageException("存档签名公钥不是 ECDSA P-256");
            }

            return key;
        }
        catch (CryptographicException exception)
        {
            throw new SnapshotPackageException("存档签名公钥无效", exception);
        }
    }

    private static void EnsureNoSensitiveData(IEnumerable<PackageFile> files)
    {
        var findings = new List<string>();
        foreach (var file in files.Where(IsTextFile))
        {
            findings.AddRange(
                file.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                    ? SensitiveDataScanner.ScanJson(file.Content.Span, file.Path)
                    : SensitiveDataScanner.ScanText(file.Content.Span, file.Path));
        }

        if (findings.Count > 0)
        {
            throw new SnapshotSensitiveDataException(string.Join(Environment.NewLine, findings));
        }
    }

    private static void EnsureSafeEntries(ZipArchive archive)
    {
        long totalLength = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith('/')
                || entry.FullName.Contains('\\')
                || entry.FullName.Split('/').Any(part => part is "." or "..")
                || !paths.Add(entry.FullName))
            {
                throw new SnapshotPackageException($"存档包含非法路径: {entry.FullName}");
            }

            if (entry.Length > MaximumEntryLength)
            {
                throw new SnapshotPackageException($"存档条目过大: {entry.FullName}");
            }

            totalLength += entry.Length;
            if (totalLength > MaximumPackageLength)
            {
                throw new SnapshotPackageException("存档解压后超过大小限制");
            }
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadMetadataEntryAsync(
        ZipArchive archive,
        string path,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path)
            ?? throw new SnapshotPackageException($"存档缺少文件: {path}");
        if (entry.Length > maximumLength)
        {
            throw new SnapshotPackageException($"存档元数据过大: {path}");
        }

        return await ReadEntryAsync(entry, cancellationToken);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadEntryAsync(
        ZipArchive archive,
        string path,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(path)
            ?? throw new SnapshotPackageException($"存档缺少文件: {path}");
        return await ReadEntryAsync(entry, cancellationToken);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadEntryAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var entryStream = await entry.OpenAsync(cancellationToken);
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await entryStream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> content, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content.Span, JsonOptions)
                ?? throw new SnapshotPackageException($"无法解析 {path}");
        }
        catch (JsonException exception)
        {
            throw new SnapshotPackageException($"无法解析 {path}", exception);
        }
    }

    private static bool IsTextFile(PackageFile file) =>
        file.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || file.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || file.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
        || file.Path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);
}
