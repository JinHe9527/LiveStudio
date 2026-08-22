using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionSnapshotProjector
{
    public static async Task<IReadOnlyList<VideoSource>> CreateSourcesAsync(
        IReadOnlyList<NativeConfigurationDocument> documents,
        CancellationToken cancellationToken)
    {
        var sources = new List<VideoSource>();
        foreach (var document in documents)
        {
            var deviceValue = document.Values.FirstOrDefault(value =>
                value.Category == NativeParameterCategories.DeviceSelection);
            var deviceName = GetScalarText(deviceValue?.Value);
            var settings = document.Values
                .Where(value => value.Category != NativeParameterCategories.Filter)
                .ToDictionary(
                    value => value.JsonPointer,
                    value => value.Value.Clone(),
                    StringComparer.Ordinal);
            var filters = await CreateFiltersAsync(document, cancellationToken);
            sources.Add(new VideoSource(
                document.SourceLogicalId,
                Path.GetFileNameWithoutExtension(document.RelativePath),
                "live_companion_video_source",
                string.IsNullOrWhiteSpace(deviceName)
                    ? null
                    : new CaptureDeviceDescriptor(deviceName, null, null, null, deviceName, []),
                CreateMode(document.Values),
                settings,
                filters));
        }

        return sources;
    }

    private static async Task<IReadOnlyList<VideoFilter>> CreateFiltersAsync(
        NativeConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        var filters = new List<VideoFilter>();
        foreach (var value in document.Values.Where(value => value.Category == NativeParameterCategories.Filter))
        {
            if (value.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.Value.EnumerateArray())
                {
                    filters.Add(await CreateFilterAsync(document, value.JsonPointer, item, index, cancellationToken));
                    index++;
                }
            }
            else
            {
                filters.Add(await CreateFilterAsync(
                    document,
                    value.JsonPointer,
                    value.Value,
                    filters.Count,
                    cancellationToken));
            }
        }

        return filters;
    }

    private static async Task<VideoFilter> CreateFilterAsync(
        NativeConfigurationDocument document,
        string pointer,
        JsonElement value,
        int index,
        CancellationToken cancellationToken)
    {
        var settings = value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["value"] = value.Clone()
            };
        var name = FindString(settings, "filterName", "name", "title") ?? $"滤镜 {index + 1}";
        var kind = FindString(settings, "filterType", "kind", "type") ?? "live_companion_filter";
        var enabled = FindBoolean(settings, "enabled", "isEnabled", "enable") ?? true;
        var order = FindInt32(settings, "order", "index", "position") ?? index;
        var assets = await CaptureAssetsAsync(value, cancellationToken);
        return new VideoFilter(
            CreateLogicalId($"live-companion|{document.RelativePath}|{pointer}|{index}"),
            name,
            kind,
            enabled,
            order,
            settings,
            assets);
    }

    private static async Task<IReadOnlyList<AssetReference>> CaptureAssetsAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        var paths = EnumerateStrings(value)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var assets = new List<AssetReference>(paths.Length);
        foreach (var path in paths)
        {
            await using var stream = File.OpenRead(path);
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            var info = new FileInfo(path);
            assets.Add(new AssetReference(
                sha256,
                info.Name,
                MediaType(info.Extension),
                info.Length,
                $"assets/{sha256}/{info.Name}",
                path));
        }

        return assets;
    }

    private static VideoMode? CreateMode(IReadOnlyList<NativeConfigurationValue> values)
    {
        var width = FindInteger(values, "width");
        var height = FindInteger(values, "height");
        if (width is null || height is null)
        {
            var resolution = FindText(values, "resolution");
            if (resolution is not null)
            {
                var parts = resolution.Split('x', 'X', '×');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var parsedWidth)
                    && int.TryParse(parts[1], out var parsedHeight))
                {
                    width = parsedWidth;
                    height = parsedHeight;
                }
            }
        }

        if (width is null || height is null)
        {
            return null;
        }

        var fps = FindInteger(values, "fps") ?? FindInteger(values, "framerate") ?? 0;
        return new VideoMode(
            width.Value,
            height.Value,
            fps,
            1,
            FindText(values, "pixelformat") ?? string.Empty,
            FindText(values, "colorspace") ?? string.Empty,
            FindText(values, "colorrange") ?? string.Empty);
    }

    private static int? FindInteger(IEnumerable<NativeConfigurationValue> values, string key) => values
        .FirstOrDefault(value => NormalizePointerLeaf(value.JsonPointer) == key)?.Value is { } element
            && element.TryGetInt32(out var number)
                ? number
                : null;

    private static string? FindText(IEnumerable<NativeConfigurationValue> values, string key) =>
        GetScalarText(values.FirstOrDefault(value => NormalizePointerLeaf(value.JsonPointer) == key)?.Value);

    private static string? GetScalarText(JsonElement? value) => value is { } element
        ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
        : null;

    private static string? FindString(
        Dictionary<string, JsonElement> settings,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (settings.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool? FindBoolean(
        Dictionary<string, JsonElement> settings,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (settings.TryGetValue(name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static int? FindInt32(
        Dictionary<string, JsonElement> settings,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (settings.TryGetValue(name, out var value) && value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value)
        {
            yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateStrings(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateStrings(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string NormalizePointerLeaf(string pointer) => string.Concat(
        pointer.Split('/').Last().Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".bmp" => "image/bmp",
        ".cube" => "application/octet-stream",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

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
