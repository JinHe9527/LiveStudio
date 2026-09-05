using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionAssetMapper
{
    private static readonly HashSet<string> KnownAssetExtensions = new(
        [".3dl", ".bmp", ".cube", ".gif", ".jpeg", ".jpg", ".look", ".lut", ".png", ".tga", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<ConfigurationTreeSnapshot> CaptureAsync(
        ConfigurationTreeSnapshot tree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var sections = new ConfigurationSectionSnapshot[tree.Sections.Count];
        for (var index = 0; index < tree.Sections.Count; index++)
        {
            sections[index] = await CaptureSectionAsync(tree.Sections[index], cancellationToken);
        }

        return tree with { Sections = sections };
    }

    public static IEnumerable<string> FindUnresolvedAssetPaths(
        IEnumerable<NativeConfigurationDocument> documents,
        IReadOnlyList<AssetBinding> assets)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(assets);
        foreach (var configuredPath in documents
                     .SelectMany(document => document.Values)
                     .SelectMany(value => EnumerateStrings(value.Value))
                     .Where(IsExternalAssetPath)
                     .Distinct(PathComparer))
        {
            if (File.Exists(configuredPath)
                || assets.Any(asset => PathsEqual(asset.SourcePath, configuredPath)))
            {
                continue;
            }

            yield return configuredPath;
        }
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (PathComparer.Equals(left, right))
        {
            return true;
        }

        try
        {
            return PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<ConfigurationSectionSnapshot> CaptureSectionAsync(
        ConfigurationSectionSnapshot section,
        CancellationToken cancellationToken)
    {
        var sections = new ConfigurationSectionSnapshot[section.Sections.Count];
        for (var index = 0; index < section.Sections.Count; index++)
        {
            sections[index] = await CaptureSectionAsync(section.Sections[index], cancellationToken);
        }

        var fields = new ConfigurationFieldSnapshot[section.Fields.Count];
        for (var index = 0; index < section.Fields.Count; index++)
        {
            fields[index] = await CaptureFieldAsync(section.Fields[index], cancellationToken);
        }

        return section with { Sections = sections, Fields = fields };
    }

    private static async Task<ConfigurationFieldSnapshot> CaptureFieldAsync(
        ConfigurationFieldSnapshot field,
        CancellationToken cancellationToken)
    {
        if (field.CurrentValue.ValueKind != JsonValueKind.String
            || field.CurrentValue.GetString() is not { } configuredPath
            || !IsExternalAssetPath(configuredPath))
        {
            return field;
        }

        if (!File.Exists(configuredPath))
        {
            throw new FileNotFoundException(
                $"直播伴侣滤镜素材不存在，无法随存档备份: {Path.GetFileName(configuredPath)}",
                configuredPath);
        }

        var sourcePath = Path.GetFullPath(configuredPath);
        await using var stream = File.OpenRead(sourcePath);
        if (stream.Length == 0)
        {
            throw new InvalidDataException(
                $"直播伴侣滤镜素材为空，无法生成可恢复存档: {Path.GetFileName(sourcePath)}");
        }
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        var info = new FileInfo(sourcePath);
        var binding = new AssetBinding(
            CreateLogicalId(
                $"live-companion-asset|{field.Locator.StoreId}|{field.Locator.NativePath}|{sourcePath}"),
            hash,
            info.Name,
            sourcePath,
            field.Locator.NativePath,
            info.Length);
        return field with { Assets = field.Assets.Append(binding).DistinctBy(asset => asset.Id).ToArray() };
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } value)
                {
                    yield return value;
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nestedValue in EnumerateStrings(property.Value))
                    {
                        yield return nestedValue;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in EnumerateStrings(item))
                    {
                        yield return nestedValue;
                    }
                }

                break;
        }
    }

    private static bool IsExternalAssetPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            return KnownAssetExtensions.Contains(Path.GetExtension(value));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Guid CreateLogicalId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> identifier = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(identifier);
        identifier[6] = (byte)((identifier[6] & 0x0F) | 0x50);
        identifier[8] = (byte)((identifier[8] & 0x3F) | 0x80);
        return new Guid(identifier);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
