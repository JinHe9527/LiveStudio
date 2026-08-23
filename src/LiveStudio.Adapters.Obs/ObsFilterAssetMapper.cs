using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.Obs;

internal static class ObsFilterAssetMapper
{
    public static async Task<IReadOnlyList<AssetBinding>> CaptureAsync(
        IReadOnlyDictionary<string, JsonElement> settings,
        CancellationToken cancellationToken)
    {
        var paths = settings
            .SelectMany(pair => EnumerateStringValues(pair.Value, $"/{EscapePointer(pair.Key)}"))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            .Select(item => (Path: Path.GetFullPath(item.Path), item.ReferencePath))
            .DistinctBy(item => $"{item.Path}\0{item.ReferencePath}",
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var assets = new List<AssetBinding>(paths.Length);
        foreach (var item in paths)
        {
            await using var stream = File.OpenRead(item.Path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var info = new FileInfo(item.Path);
            var hashText = Convert.ToHexStringLower(hash);
            assets.Add(new AssetBinding(
                CreateLogicalId($"obs-asset|{item.Path}|{item.ReferencePath}"),
                hashText,
                info.Name,
                item.Path,
                item.ReferencePath));
        }

        return assets;
    }

    public static Dictionary<string, JsonElement> Materialize(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyList<AssetBinding> assets,
        string assetDirectory) => settings.ToDictionary(
            pair => pair.Key,
            pair => MaterializeValue(pair.Value, assets, assetDirectory),
            StringComparer.Ordinal);

    private static IEnumerable<(string Path, string ReferencePath)> EnumerateStringValues(
        JsonElement element,
        string pointer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } value)
                {
                    yield return (value, pointer);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nestedValue in EnumerateStringValues(
                                 property.Value,
                                 $"{pointer}/{EscapePointer(property.Name)}"))
                    {
                        yield return nestedValue;
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in EnumerateStringValues(item, $"{pointer}/{index}"))
                    {
                        yield return nestedValue;
                    }

                    index++;
                }

                break;
        }
    }

    private static JsonElement MaterializeValue(
        JsonElement value,
        IReadOnlyList<AssetBinding> assets,
        string assetDirectory)
    {
        var node = JsonNode.Parse(value.GetRawText());
        var rewritten = RewriteNode(node, assets, assetDirectory);
        return JsonSerializer.SerializeToElement(rewritten);
    }

    private static JsonNode? RewriteNode(
        JsonNode? node,
        IReadOnlyList<AssetBinding> assets,
        string assetDirectory)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var sourcePath))
        {
            var asset = assets.FirstOrDefault(candidate =>
                string.Equals(candidate.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                return node.DeepClone();
            }

            var materializedPath = Path.Combine(assetDirectory, asset.BlobSha256, asset.OriginalFileName);
            return File.Exists(materializedPath) ? JsonValue.Create(materializedPath) : node.DeepClone();
        }

        if (node is JsonObject jsonObject)
        {
            var rewrittenObject = new JsonObject();
            foreach (var property in jsonObject)
            {
                rewrittenObject[property.Key] = RewriteNode(property.Value, assets, assetDirectory);
            }

            return rewrittenObject;
        }

        if (node is JsonArray jsonArray)
        {
            var rewrittenArray = new JsonArray();
            foreach (var item in jsonArray)
            {
                rewrittenArray.Add(RewriteNode(item, assets, assetDirectory));
            }

            return rewrittenArray;
        }

        return node?.DeepClone();
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static Guid CreateLogicalId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> identifier = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(identifier);
        identifier[6] = (byte)((identifier[6] & 0x0F) | 0x50);
        identifier[8] = (byte)((identifier[8] & 0x3F) | 0x80);
        return new Guid(identifier);
    }
}
