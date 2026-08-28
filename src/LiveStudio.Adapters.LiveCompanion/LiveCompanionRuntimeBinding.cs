using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

/// <summary>
/// 将签名定义里的规范运行时标识绑定到当前直播伴侣生成的来源与效果标识。
/// 绑定只在签名结构和当前原生树都恰好包含一个摄像头时成立；无法唯一确定时拒绝猜测。
/// </summary>
internal sealed class LiveCompanionRuntimeBinding
{
    private readonly IReadOnlyDictionary<string, string> canonicalToRuntime;
    private readonly IReadOnlyDictionary<string, string> runtimeToCanonical;

    private LiveCompanionRuntimeBinding(Dictionary<string, string> canonicalToRuntime)
    {
        this.canonicalToRuntime = canonicalToRuntime;
        runtimeToCanonical = canonicalToRuntime.ToDictionary(
            item => item.Value,
            item => item.Key,
            StringComparer.Ordinal);
    }

    public static LiveCompanionRuntimeBinding? TryCreate(
        LiveCompanionAdapterDefinition definition,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var canonicalCameras = definition.Fields
            .Where(field => string.Equals(field.StoreId, "source-store", StringComparison.Ordinal))
            .Select(field => ParseCameraPath(field.NativePath))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct()
            .ToArray();
        if (canonicalCameras.Length == 0)
        {
            return new LiveCompanionRuntimeBinding([]);
        }

        var sourceStore = discoveredDocuments.SingleOrDefault(document => string.Equals(
            Path.GetFileName(document.RelativePath),
            "sourceStore.json",
            StringComparison.OrdinalIgnoreCase));
        if (sourceStore is null)
        {
            return null;
        }

        var values = sourceStore.Values.ToDictionary(
            value => value.JsonPointer,
            StringComparer.Ordinal);
        var runtimeCameras = sourceStore.Values
            .Where(value => value.Value.ValueKind == JsonValueKind.String
                            && string.Equals(value.Value.GetString(), "camera", StringComparison.Ordinal))
            .Select(value => ParseCameraTypePath(value.JsonPointer))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct()
            .ToArray();
        if (canonicalCameras.ToHashSet().SetEquals(runtimeCameras))
        {
            var identityEffectIds = definition.Fields
                .Where(field => string.Equals(field.StoreId, "effect-config", StringComparison.Ordinal))
                .Select(field => ParseEffectConfigurationId(field.NativePath))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var runtimeEffectIds = runtimeCameras
                .Select(camera =>
                {
                    var pointer = $"/sourceStore/sceneSource/{EscapePointer(camera.SceneId)}/data/{EscapePointer(camera.SourceId)}/effectConfigId";
                    return values.TryGetValue(pointer, out var effectValue)
                           && effectValue.Value.ValueKind == JsonValueKind.String
                        ? effectValue.Value.GetString()
                        : null;
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal);
            return identityEffectIds.SetEquals(runtimeEffectIds)
                ? new LiveCompanionRuntimeBinding([])
                : null;
        }

        if (canonicalCameras.Length != 1 || runtimeCameras.Length != 1)
        {
            return null;
        }

        var canonical = canonicalCameras[0];
        var runtime = runtimeCameras[0];
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [canonical.SceneId] = runtime.SceneId,
            [canonical.SourceId] = runtime.SourceId
        };

        var runtimeEffectPointer = $"/sourceStore/sceneSource/{EscapePointer(runtime.SceneId)}/data/{EscapePointer(runtime.SourceId)}/effectConfigId";
        var runtimeEffectId = values.TryGetValue(runtimeEffectPointer, out var effectValue)
            && effectValue.Value.ValueKind == JsonValueKind.String
                ? effectValue.Value.GetString()
                : null;
        var canonicalEffectIds = definition.Fields
            .Where(field => string.Equals(field.StoreId, "effect-config", StringComparison.Ordinal))
            .Select(field => ParseEffectConfigurationId(field.NativePath))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (canonicalEffectIds.Length == 1 && !string.IsNullOrWhiteSpace(runtimeEffectId))
        {
            mapping[canonicalEffectIds[0]!] = runtimeEffectId;
        }
        else if (canonicalEffectIds.Length != 0)
        {
            return null;
        }

        return new LiveCompanionRuntimeBinding(mapping);
    }

    public string ToRuntimePointer(string canonicalPointer) => TranslatePointer(
        canonicalPointer,
        canonicalToRuntime);

    public JsonElement ToCanonicalValue(JsonElement runtimeValue)
    {
        var node = ReplaceIdentifiers(JsonNode.Parse(runtimeValue.GetRawText()), runtimeToCanonical);
        return JsonSerializer.SerializeToElement(node);
    }

    private static CameraPath? ParseCameraPath(string pointer)
    {
        var segments = PointerSegments(pointer);
        return segments.Length >= 5
               && string.Equals(segments[0], "sourceStore", StringComparison.Ordinal)
               && string.Equals(segments[1], "sceneSource", StringComparison.Ordinal)
               && string.Equals(segments[3], "data", StringComparison.Ordinal)
            ? new CameraPath(segments[2], segments[4])
            : null;
    }

    private static CameraPath? ParseCameraTypePath(string pointer)
    {
        var segments = PointerSegments(pointer);
        return segments.Length == 6
               && string.Equals(segments[0], "sourceStore", StringComparison.Ordinal)
               && string.Equals(segments[1], "sceneSource", StringComparison.Ordinal)
               && string.Equals(segments[3], "data", StringComparison.Ordinal)
               && string.Equals(segments[5], "type", StringComparison.Ordinal)
            ? new CameraPath(segments[2], segments[4])
            : null;
    }

    private static string? ParseEffectConfigurationId(string pointer)
    {
        var segments = PointerSegments(pointer);
        return segments.Length >= 4
               && string.Equals(segments[0], "effectConfigStore", StringComparison.Ordinal)
               && string.Equals(segments[1], "configs", StringComparison.Ordinal)
            ? segments[2]
            : null;
    }

    private static string TranslatePointer(
        string pointer,
        IReadOnlyDictionary<string, string> replacements) => "/" + string.Join(
        '/',
        PointerSegments(pointer).Select(segment => EscapePointer(
            replacements.TryGetValue(segment, out var replacement)
                ? replacement
                : segment)));

    private static JsonNode? ReplaceIdentifiers(
        JsonNode? node,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && replacements.TryGetValue(text, out var replacement))
        {
            return JsonValue.Create(replacement);
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
            {
                result.Add(ReplaceIdentifiers(item, replacements));
            }

            return result;
        }

        if (node is JsonObject objectValue)
        {
            var result = new JsonObject();
            foreach (var property in objectValue)
            {
                var name = replacements.TryGetValue(property.Key, out var propertyReplacement)
                    ? propertyReplacement
                    : property.Key;
                result[name] = ReplaceIdentifiers(property.Value, replacements);
            }

            return result;
        }

        return node?.DeepClone();
    }

    private static string[] PointerSegments(string pointer) => pointer
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(UnescapePointer)
        .ToArray();

    private static string EscapePointer(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string UnescapePointer(string value) => value
        .Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private sealed record CameraPath(string SceneId, string SourceId);
}
