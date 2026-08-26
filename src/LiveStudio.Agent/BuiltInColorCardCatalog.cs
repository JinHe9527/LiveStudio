using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Adapters.Obs;

namespace LiveStudio.Agent;

public sealed class BuiltInColorCardCatalog : IObsAssetPathResolver
{
    internal const int ExpectedAssetCount = 54;
    internal const string ExpectedBundleId = "color-cards-v1";
    internal const string ExpectedSourceArchiveSha256 =
        "2f06cd00ee745207ca5b352bf4edbe3f8c738d2d11e04a3f46a730ee1bd3cf33";
    internal const string ExpectedManifestSha256 =
        "59b8ffd612a44e5080bbd38476fc8c1f8e1636ba9f7eda8a8ff95f218c56f63a";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string bundleRoot;
    private readonly string materializedRoot;
    private IReadOnlyDictionary<string, BuiltInColorCardAsset>? assetsByName;

    public BuiltInColorCardCatalog()
        : this(
            Path.Combine(AppContext.BaseDirectory, "BuiltInAssets", "ColorCards", "v1"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveStudio",
                "OBS素材",
                "色卡",
                "v1"))
    {
    }

    internal BuiltInColorCardCatalog(string bundleRoot)
        : this(bundleRoot, bundleRoot)
    {
    }

    internal BuiltInColorCardCatalog(string bundleRoot, string materializedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(materializedRoot);
        this.bundleRoot = Path.GetFullPath(bundleRoot);
        this.materializedRoot = Path.GetFullPath(materializedRoot);
    }

    public async Task EnsureIntegrityAsync(CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(bundleRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"内置色卡清单不存在: {manifestPath}");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (!string.Equals(manifestHash, ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("内置色卡清单哈希不符合当前产品定义");
        }

        var manifest = JsonSerializer.Deserialize<BuiltInColorCardManifest>(
            manifestBytes,
            JsonOptions)
            ?? throw new InvalidDataException("内置色卡清单为空");
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.BundleId, ExpectedBundleId, StringComparison.Ordinal)
            || !string.Equals(
                manifest.SourceArchiveSha256,
                ExpectedSourceArchiveSha256,
                StringComparison.OrdinalIgnoreCase)
            || manifest.Assets.Count != ExpectedAssetCount)
        {
            throw new InvalidDataException("内置色卡清单版本、来源或素材数量不符合当前产品定义");
        }

        var validated = new Dictionary<string, BuiltInColorCardAsset>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMetadata(asset);
            if (!identifiers.Add(asset.Id)
                || !validated.TryAdd(NormalizeName(asset.Name), asset))
            {
                throw new InvalidDataException($"内置色卡清单存在重复 ID 或文件名: {asset.Name}");
            }

            var path = ResolveContainedPath(bundleRoot, asset.RelativePath);
            await ValidateFileAsync(path, asset, cancellationToken);
        }

        foreach (var asset in validated.Values)
        {
            await MaterializeFileAsync(asset, cancellationToken);
        }

        assetsByName = validated;
    }

    public string? ResolveMissingPath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var catalog = assetsByName
            ?? throw new InvalidOperationException("内置色卡尚未完成启动完整性校验");
        string fileName;
        try
        {
            fileName = Path.GetFileName(configuredPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(fileName)
            || !catalog.TryGetValue(NormalizeName(fileName), out var asset))
        {
            return null;
        }

        var path = ResolveContainedPath(materializedRoot, asset.RelativePath);
        if (!IsFileValid(path, asset))
        {
            MaterializeFile(asset);
        }

        ValidateFile(path, asset);
        return path;
    }

    private static void ValidateMetadata(BuiltInColorCardAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Id)
            || string.IsNullOrWhiteSpace(asset.Name)
            || !string.Equals(Path.GetFileName(asset.Name), asset.Name, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(asset.RelativePath)
            || asset.Length <= 0
            || asset.Sha256.Length != 64
            || asset.Sha256.Any(character => !Uri.IsHexDigit(character))
            || asset.MediaType is not ("image/png" or "application/x-cube"))
        {
            throw new InvalidDataException($"内置色卡清单项无效: {asset.Name}");
        }

        var expectedExtension = asset.MediaType == "image/png" ? ".png" : ".cube";
        if (!string.Equals(Path.GetExtension(asset.Name), expectedExtension, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetExtension(asset.RelativePath),
                expectedExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"内置色卡类型与扩展名不一致: {asset.Name}");
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"内置色卡路径必须是相对路径: {relativePath}");
        }

        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"内置色卡路径越界: {relativePath}");
        }

        return path;
    }

    private async Task MaterializeFileAsync(
        BuiltInColorCardAsset asset,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveContainedPath(bundleRoot, asset.RelativePath);
        var destinationPath = ResolveContainedPath(materializedRoot, asset.RelativePath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            || await IsFileValidAsync(destinationPath, asset, cancellationToken))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"无法确定 OBS 色卡目录: {asset.Name}");
        Directory.CreateDirectory(directory);
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
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            await ValidateFileAsync(destinationPath, asset, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void MaterializeFile(BuiltInColorCardAsset asset)
    {
        var sourcePath = ResolveContainedPath(bundleRoot, asset.RelativePath);
        var destinationPath = ResolveContainedPath(materializedRoot, asset.RelativePath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException($"无法确定 OBS 色卡目录: {asset.Name}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{destinationPath}.partial-{Guid.NewGuid():N}";
        try
        {
            using (var source = File.OpenRead(sourcePath))
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131_072,
                FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
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

    private static async Task<bool> IsFileValidAsync(
        string path,
        BuiltInColorCardAsset asset,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        if (stream.Length != asset.Length)
        {
            return false;
        }

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileValid(string path, BuiltInColorCardAsset asset)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Length)
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ValidateFileAsync(
        string path,
        BuiltInColorCardAsset asset,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"内置色卡文件缺失: {asset.Name}");
        }

        await using var stream = File.OpenRead(path);
        if (stream.Length != asset.Length)
        {
            throw new InvalidDataException($"内置色卡文件长度不一致: {asset.Name}");
        }

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"内置色卡文件哈希不一致: {asset.Name}");
        }
    }

    private static void ValidateFile(string path, BuiltInColorCardAsset asset)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"内置色卡文件缺失: {asset.Name}");
        }

        var info = new FileInfo(path);
        if (info.Length != asset.Length)
        {
            throw new InvalidDataException($"内置色卡文件长度不一致: {asset.Name}");
        }

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"内置色卡文件哈希不一致: {asset.Name}");
        }
    }

    private static string NormalizeName(string value) => value.Normalize(NormalizationForm.FormC);

    private sealed record BuiltInColorCardManifest(
        int SchemaVersion,
        string BundleId,
        string SourceArchiveSha256,
        IReadOnlyList<BuiltInColorCardAsset> Assets);

    private sealed record BuiltInColorCardAsset(
        string Id,
        string Name,
        string RelativePath,
        string MediaType,
        long Length,
        string Sha256);
}
