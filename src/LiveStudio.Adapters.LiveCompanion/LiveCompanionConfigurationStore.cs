using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal sealed class LiveCompanionConfigurationStore(string? rootPath = null)
{
    private const long MaximumConfigurationFileLength = 64L * 1024 * 1024;
    private static readonly string[] ExcludedPathTerms =
    [
        "account", "auth", "cache", "cookie", "credential", "crash", "gpu", "log", "login",
        "network", "session", "storage", "telemetry", "temp", "token", "user"
    ];
    private static readonly string[] SensitiveTerms =
    [
        "account", "authorization", "cookie", "credential", "did", "login", "oauth", "password",
        "passport", "secret", "session", "streamkey", "token", "uid"
    ];
    private static readonly string[] FilterTerms =
    [
        "beauty", "brightness", "chroma", "contrast", "effect", "exposure", "filter", "gamma",
        "keying", "lut", "mask", "saturation", "sharp", "smooth", "whiten"
    ];
    private static readonly string[] DeviceTerms =
    [
        "cameraid", "capturedevice", "deviceid", "videodevice", "videoinput"
    ];
    private static readonly string[] ModeTerms =
    [
        "colorrange", "colorspace", "fps", "framerate", "height", "pixelformat", "resolution", "width"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string RootPath { get; } = Path.GetFullPath(rootPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "webcast_mate"));

    public async Task<IReadOnlyList<NativeConfigurationDocument>> CaptureDocumentsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        var documents = new List<NativeConfigurationDocument>();
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(RootPath, path);
            if (ShouldExcludePath(relativePath))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumConfigurationFileLength)
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    131_072,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var values = new List<NativeConfigurationValue>();
                CollectValues(json.RootElement, string.Empty, values);
                if (values.Count == 0)
                {
                    continue;
                }

                var ordered = values
                    .OrderBy(value => value.JsonPointer, StringComparer.Ordinal)
                    .ToArray();
                var content = JsonSerializer.SerializeToUtf8Bytes(ordered, JsonOptions);
                var sourceId = CreateLogicalId($"live-companion|{relativePath}");
                documents.Add(new NativeConfigurationDocument(
                    "webcast_mate",
                    relativePath,
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    sourceId,
                    ordered));
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return documents;
    }

    public async Task<LiveCompanionLiveState> InspectLiveStateAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootPath))
        {
            return new LiveCompanionLiveState(false, false);
        }

        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldExcludePath(Path.GetRelativePath(RootPath, path)))
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    65_536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (TryFindLiveState(json.RootElement, out var isLive))
                {
                    return new LiveCompanionLiveState(isLive, true);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return new LiveCompanionLiveState(false, false);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> BackupAsync(
        IEnumerable<NativeConfigurationDocument> documents,
        CancellationToken cancellationToken)
    {
        var backup = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            var path = ResolveDocumentPath(document);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"直播伴侣配置文件不存在: {document.RelativePath}", path);
            }

            backup.Add(path, await File.ReadAllBytesAsync(path, cancellationToken));
        }

        return backup;
    }

    public async Task ApplyAsync(
        IReadOnlyList<NativeConfigurationDocument> documents,
        IReadOnlyDictionary<Guid, DeviceMapping> mappings,
        IReadOnlyList<AssetReference> assets,
        string assetDirectory,
        CancellationToken cancellationToken)
    {
        var expectedDocuments = LiveCompanionConfigurationStore.CreateExpectedDocuments(
            documents,
            mappings,
            assets,
            assetDirectory);
        foreach (var document in expectedDocuments)
        {
            var path = ResolveDocumentPath(document);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"直播伴侣配置文件不存在: {document.RelativePath}", path);
            }

            JsonNode root;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             131_072,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
                    ?? throw new JsonException($"无法解析直播伴侣配置: {document.RelativePath}");
            }

            foreach (var value in document.Values)
            {
                SetPointer(root, value.JsonPointer, JsonNode.Parse(value.Value.GetRawText()));
            }

            await WriteJsonAtomicallyAsync(path, root, cancellationToken);
        }
    }

    public static IReadOnlyList<NativeConfigurationDocument> CreateExpectedDocuments(
        IReadOnlyList<NativeConfigurationDocument> documents,
        IReadOnlyDictionary<Guid, DeviceMapping> mappings,
        IReadOnlyList<AssetReference> assets,
        string assetDirectory) => documents.Select(document =>
    {
        mappings.TryGetValue(document.SourceLogicalId, out var mapping);
        var values = document.Values.Select(value =>
        {
            JsonNode? targetValue = value.Category == NativeParameterCategories.DeviceSelection && mapping is not null
                ? JsonValue.Create(mapping.TargetDeviceId)
                : JsonNode.Parse(value.Value.GetRawText());
            targetValue = MaterializeAssetPaths(targetValue, assets, assetDirectory);
            return value with { Value = JsonSerializer.SerializeToElement(targetValue, JsonOptions) };
        }).ToArray();
        var content = JsonSerializer.SerializeToUtf8Bytes(values, JsonOptions);
        return document with
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Values = values
        };
    }).ToArray();

    public static async Task RestoreBackupAsync(
        IReadOnlyDictionary<string, byte[]> backup,
        CancellationToken cancellationToken)
    {
        foreach (var item in backup)
        {
            await WriteBytesAtomicallyAsync(item.Key, item.Value, cancellationToken);
        }
    }

    public string ResolveDocumentPath(NativeConfigurationDocument document)
    {
        if (!string.Equals(document.StoreId, "webcast_mate", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"不支持的直播伴侣存储: {document.StoreId}");
        }

        var path = Path.GetFullPath(Path.Combine(RootPath, document.RelativePath));
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"直播伴侣配置路径越界: {document.RelativePath}");
        }

        return path;
    }

    private static void CollectValues(
        JsonElement element,
        string pointer,
        ICollection<NativeConfigurationValue> destination)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = Normalize(property.Name);
                if (ContainsAny(normalized, SensitiveTerms))
                {
                    continue;
                }

                var childPointer = $"{pointer}/{EscapePointer(property.Name)}";
                var category = Classify(normalized);
                if (category is not null)
                {
                    var sanitized = Sanitize(property.Value);
                    if (sanitized is not null)
                    {
                        destination.Add(new NativeConfigurationValue(
                            childPointer,
                            category,
                            JsonSerializer.SerializeToElement(sanitized, JsonOptions)));
                    }

                    continue;
                }

                CollectValues(property.Value, childPointer, destination);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                CollectValues(item, $"{pointer}/{index}", destination);
                index++;
            }
        }
    }

    private static JsonNode? Sanitize(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
            {
                if (ContainsAny(Normalize(property.Name), SensitiveTerms))
                {
                    continue;
                }

                result[property.Name] = Sanitize(property.Value);
            }

            return result;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in element.EnumerateArray())
            {
                result.Add(Sanitize(item));
            }

            return result;
        }

        return JsonNode.Parse(element.GetRawText());
    }

    private static string? Classify(string normalizedName)
    {
        if (ContainsAny(normalizedName, FilterTerms))
        {
            return NativeParameterCategories.Filter;
        }

        if (DeviceTerms.Any(term => string.Equals(normalizedName, term, StringComparison.Ordinal)))
        {
            return NativeParameterCategories.DeviceSelection;
        }

        return ModeTerms.Any(term => string.Equals(normalizedName, term, StringComparison.Ordinal))
            ? NativeParameterCategories.VideoMode
            : null;
    }

    private static bool TryFindLiveState(JsonElement element, out bool isLive)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = Normalize(property.Name);
                if (name is "islive" or "isstreaming" or "livestatus" or "streamstatus" or "broadcaststatus"
                    && TryInterpretLiveValue(property.Value, out isLive))
                {
                    return true;
                }

                if (TryFindLiveState(property.Value, out isLive))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindLiveState(item, out isLive))
                {
                    return true;
                }
            }
        }

        isLive = false;
        return false;
    }

    private static bool TryInterpretLiveValue(JsonElement value, out bool isLive)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            isLive = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            isLive = number != 0;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = Normalize(value.GetString() ?? string.Empty);
            if (text is "idle" or "offline" or "ready" or "stopped" or "notlive")
            {
                isLive = false;
                return true;
            }

            if (text is "live" or "living" or "online" or "streaming" or "broadcasting")
            {
                isLive = true;
                return true;
            }
        }

        isLive = false;
        return false;
    }

    private static void SetPointer(JsonNode root, string pointer, JsonNode? value)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapePointer)
            .ToArray();
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("不允许覆盖直播伴侣配置根节点");
        }

        JsonNode current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = current switch
            {
                JsonObject jsonObject => jsonObject[segments[index]]
                    ?? throw new InvalidOperationException($"配置路径不存在: {pointer}"),
                JsonArray jsonArray when int.TryParse(segments[index], out var arrayIndex)
                                         && arrayIndex >= 0
                                         && arrayIndex < jsonArray.Count => jsonArray[arrayIndex]
                    ?? throw new InvalidOperationException($"配置路径不存在: {pointer}"),
                _ => throw new InvalidOperationException($"配置路径结构不匹配: {pointer}")
            };
        }

        var leaf = segments[^1];
        if (current is JsonObject targetObject)
        {
            if (!targetObject.ContainsKey(leaf))
            {
                throw new InvalidOperationException($"配置字段不存在: {pointer}");
            }

            targetObject[leaf] = value;
            return;
        }

        if (current is JsonArray targetArray
            && int.TryParse(leaf, out var targetIndex)
            && targetIndex >= 0
            && targetIndex < targetArray.Count)
        {
            targetArray[targetIndex] = value;
            return;
        }

        throw new InvalidOperationException($"配置字段结构不匹配: {pointer}");
    }

    private static JsonNode? MaterializeAssetPaths(
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

            var targetPath = Path.Combine(assetDirectory, asset.Sha256, asset.OriginalFileName);
            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException($"滤镜素材尚未物化: {asset.OriginalFileName}", targetPath);
            }

            return JsonValue.Create(targetPath);
        }

        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach (var property in jsonObject)
            {
                result[property.Key] = MaterializeAssetPaths(property.Value, assets, assetDirectory);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var item in jsonArray)
            {
                result.Add(MaterializeAssetPaths(item, assets, assetDirectory));
            }

            return result;
        }

        return node?.DeepClone();
    }

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        JsonNode content,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content, JsonOptions);
        await WriteBytesAtomicallyAsync(path, bytes, cancellationToken);
    }

    private static async Task WriteBytesAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.livestudio-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool ShouldExcludePath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => ContainsAny(Normalize(part), ExcludedPathTerms));
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    private static string Normalize(string value) => string.Concat(
        value.Where(character => char.IsLetterOrDigit(character))).ToLowerInvariant();

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string UnescapePointer(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private static Guid CreateLogicalId(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        Span<byte> identifier = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(identifier);
        identifier[6] = (byte)((identifier[6] & 0x0F) | 0x50);
        identifier[8] = (byte)((identifier[8] & 0x3F) | 0x80);
        return new Guid(identifier);
    }
}

internal sealed record LiveCompanionLiveState(bool IsLive, bool CanDetermine);

internal static class NativeParameterCategories
{
    public const string DeviceSelection = "DeviceSelection";
    public const string VideoMode = "VideoMode";
    public const string Filter = "Filter";
}
