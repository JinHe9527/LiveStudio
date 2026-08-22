using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.Obs;

internal static class ObsFilterAssetMapper
{
    public static async Task<IReadOnlyList<AssetReference>> CaptureAsync(
        IReadOnlyDictionary<string, JsonElement> settings,
        CancellationToken cancellationToken)
    {
        var paths = settings.Values
            .SelectMany(EnumerateStringValues)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var assets = new List<AssetReference>(paths.Length);
        foreach (var path in paths)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var info = new FileInfo(path);
            var hashText = Convert.ToHexStringLower(hash);
            assets.Add(new AssetReference(
                hashText,
                info.Name,
                GetMediaType(info.Extension),
                info.Length,
                $"assets/{hashText}/{info.Name}",
                path));
        }

        return assets;
    }

    public static Dictionary<string, JsonElement> Materialize(
        IReadOnlyDictionary<string, JsonElement> settings,
        IReadOnlyList<AssetReference> assets,
        string assetDirectory) => settings.ToDictionary(
            pair => pair.Key,
            pair => MaterializeValue(pair.Value, assets, assetDirectory),
            StringComparer.Ordinal);

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
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
                    foreach (var nestedValue in EnumerateStringValues(property.Value))
                    {
                        yield return nestedValue;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in EnumerateStringValues(item))
                    {
                        yield return nestedValue;
                    }
                }

                break;
        }
    }

    private static JsonElement MaterializeValue(
        JsonElement value,
        IReadOnlyList<AssetReference> assets,
        string assetDirectory)
    {
        var node = JsonNode.Parse(value.GetRawText());
        var rewritten = RewriteNode(node, assets, assetDirectory);
        return JsonSerializer.SerializeToElement(rewritten);
    }

    private static JsonNode? RewriteNode(
        JsonNode? node,
        IReadOnlyList<AssetReference> assets,
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

            var materializedPath = Path.Combine(assetDirectory, asset.Sha256, asset.OriginalFileName);
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

    private static string GetMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tga" => "image/x-tga",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
