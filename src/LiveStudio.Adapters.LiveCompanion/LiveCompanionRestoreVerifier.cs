using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionRestoreVerifier
{
    internal static async Task<IReadOnlyList<string>> VerifyAllAsync(
        string rootPath,
        IReadOnlyList<NativeConfigurationDocument> expectedDocuments,
        LiveCompanionCameraTarget expectedCamera,
        IReadOnlyList<LiveCompanionActiveCamera> cameras,
        CancellationToken cancellationToken)
    {
        if (cameras.Count == 0) { return ["没有可回读的目标摄像头"]; }
        var differences = new List<string>();
        foreach (var camera in cameras)
        {
            var result = await VerifyAsync(rootPath, expectedDocuments, expectedCamera, camera, cancellationToken);
            differences.AddRange(result.Select(difference => $"{camera.SceneId}/{camera.SourceId}: {difference}"));
        }
        return differences;
    }

    public static async Task<IReadOnlyList<string>> VerifyAsync(
        string rootPath,
        IReadOnlyList<NativeConfigurationDocument> expectedDocuments,
        LiveCompanionCameraTarget expectedCamera,
        LiveCompanionActiveCamera activeCamera,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [expectedCamera.SceneId] = activeCamera.SceneId,
            [expectedCamera.SourceId] = activeCamera.SourceId,
            [expectedCamera.EffectConfigurationId] = activeCamera.EffectConfigurationId
        };
        var differences = new List<string>();
        foreach (var document in expectedDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(rootPath, document.RelativePath));
            if (!File.Exists(path))
            {
                differences.Add($"缺少配置文件 {document.RelativePath}");
                continue;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var actualRoot = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var arrayLengths = GetExpectedArrayLengths(actualRoot.RootElement,
                document.Values.Select(value => TranslatePointer(value.JsonPointer, replacements)));
            foreach (var expected in document.Values)
            {
                var translatedPointer = TranslatePointer(expected.JsonPointer, replacements);
                if (!LiveCompanionConfigurationStore.TryGetPointer(
                        actualRoot.RootElement,
                        translatedPointer,
                        out var actual))
                {
                    differences.Add($"{document.RelativePath}:{translatedPointer} 缺失");
                    continue;
                }

                var normalizedExpected = ReplaceIdentifiers(
                    JsonNode.Parse(expected.Value.GetRawText()),
                    replacements);
                var actualNode = JsonNode.Parse(actual.GetRawText());
                if (!JsonNode.DeepEquals(normalizedExpected, actualNode))
                {
                    differences.Add($"{document.RelativePath}:{translatedPointer} 不一致");
                }
            }
            foreach (var (pointer, expectedLength) in arrayLengths)
            {
                if (LiveCompanionConfigurationStore.TryGetPointer(actualRoot.RootElement, pointer, out var array)
                    && array.GetArrayLength() != expectedLength)
                {
                    differences.Add($"{document.RelativePath}:{pointer} 数组长度不一致（目标 {expectedLength}，实际 {array.GetArrayLength()}）");
                }
            }
        }

        return differences;
    }

    internal static Dictionary<string, int> GetExpectedArrayLengths(JsonElement actualRoot, IEnumerable<string> pointers)
    {
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pointer in pointers)
        {
            var segments = pointer.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                if (!int.TryParse(segments[index], out var itemIndex) || itemIndex < 0) { continue; }
                var arrayPointer = string.Join('/', segments.Take(index));
                if (LiveCompanionConfigurationStore.TryGetPointer(actualRoot, arrayPointer, out var container)
                    && container.ValueKind == JsonValueKind.Array)
                {
                    lengths[arrayPointer] = Math.Max(lengths.GetValueOrDefault(arrayPointer), checked(itemIndex + 1));
                }
            }
        }
        return lengths;
    }

    private static string TranslatePointer(
        string pointer,
        Dictionary<string, string> replacements)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapePointer)
            .Select(segment => replacements.TryGetValue(segment, out var replacement)
                ? replacement
                : segment)
            .Select(EscapePointer);
        return "/" + string.Join('/', segments);
    }

    private static JsonNode? ReplaceIdentifiers(
        JsonNode? node,
        Dictionary<string, string> replacements)
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

    private static string EscapePointer(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string UnescapePointer(string value) => value
        .Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);
}
