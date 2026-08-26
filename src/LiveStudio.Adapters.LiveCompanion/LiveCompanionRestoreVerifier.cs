using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionRestoreVerifier
{
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
        }

        return differences;
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
