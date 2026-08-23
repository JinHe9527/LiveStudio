using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

public sealed class SnapshotPackageWriter
{
    private const string ParametersPath = "parameters.json";
    private const string ManifestPath = "manifest.json";
    private const string SignaturePath = "signature.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteAsync(
        string targetPath,
        CombinedSnapshot snapshot,
        IEnumerable<PackageFile> files,
        ECDsa signingKey,
        string keyId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (!string.Equals(Path.GetExtension(targetPath), ".lscfg", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotPackageException("存档文件必须使用 .lscfg 扩展名");
        }

        var packageFiles = files.ToDictionary(file => NormalizePackagePath(file.Path), StringComparer.Ordinal);
        RejectReservedPaths(packageFiles.Keys);

        var parameters = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var sensitiveFindings = SensitiveDataScanner.ScanJson(parameters, ParametersPath).ToList();
        foreach (var file in packageFiles.Values.Where(IsTextFile))
        {
            sensitiveFindings.AddRange(
                file.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                    ? SensitiveDataScanner.ScanJson(file.Content.Span, file.Path)
                    : SensitiveDataScanner.ScanText(file.Content.Span, file.Path));
        }

        if (sensitiveFindings.Count > 0)
        {
            throw new SnapshotPackageException(string.Join(Environment.NewLine, sensitiveFindings));
        }

        packageFiles.Add(ParametersPath, new PackageFile(ParametersPath, "application/json", parameters));
        var entries = packageFiles.Values
            .Select(file => new PackageFileEntry(
                file.Path,
                Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)),
                file.Content.Length,
                file.MediaType))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var manifest = new SnapshotPackageManifest(
            snapshot.Id,
            snapshot.OrganizationId,
            snapshot.RoomId,
            snapshot.Name,
            snapshot.CreatedAt,
            snapshot.SchemaVersion,
            snapshot.Applications.Select(application => new SnapshotApplicationManifest(
                application.Kind,
                application.AdapterId,
                application.AdapterDefinitionSha256,
                application.StructureFingerprint,
                application.Compatibility,
                application.WasRunning,
                application.FieldCoverage.Select(field => field.NativePath).ToArray())).ToArray(),
            snapshot.Applications.SelectMany(application => application.Sources)
                .SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .ToArray(),
            entries);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var signature = new PackageSignature(
            "ECDSA-P256-SHA256",
            keyId,
            signingKey.ExportSubjectPublicKeyInfoPem(),
            Convert.ToBase64String(signingKey.SignData(manifestBytes, HashAlgorithmName.SHA256)));
        var signatureBytes = JsonSerializer.SerializeToUtf8Bytes(signature, JsonOptions);

        var fullTargetPath = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new SnapshotPackageException("无法确定存档目录");
        Directory.CreateDirectory(targetDirectory);
        var partialPath = $"{fullTargetPath}.partial-{Guid.NewGuid():N}";

        try
        {
            await using (var stream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                131_072,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteEntryAsync(archive, ManifestPath, manifestBytes, cancellationToken);
                await WriteEntryAsync(archive, SignaturePath, signatureBytes, cancellationToken);
                foreach (var file in packageFiles.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    await WriteEntryAsync(archive, file.Path, file.Content, cancellationToken);
                }
            }

            File.Move(partialPath, fullTargetPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            throw;
        }
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
    }

    private static string NormalizePackagePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new SnapshotPackageException($"非法存档路径: {path}");
        }

        return string.Join('/', parts);
    }

    private static void RejectReservedPaths(IEnumerable<string> paths)
    {
        var reserved = new HashSet<string>([ManifestPath, SignaturePath, ParametersPath], StringComparer.Ordinal);
        var conflict = paths.FirstOrDefault(reserved.Contains);
        if (conflict is not null)
        {
            throw new SnapshotPackageException($"存档路径由系统保留: {conflict}");
        }
    }

    private static bool IsTextFile(PackageFile file) =>
        file.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || file.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || file.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
        || file.Path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);
}
