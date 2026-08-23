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
                    "JsonFile",
                    "json-v1",
                    relativePath,
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

    public async Task<IReadOnlyList<NativeConfigurationDocument>> CaptureDefinedDocumentsAsync(
        VerifiedAdapterDefinition adapter,
        CancellationToken cancellationToken)
    {
        var documents = new List<NativeConfigurationDocument>();
        foreach (var store in adapter.Definition.Stores.OrderBy(store => store.Id, StringComparer.Ordinal))
        {
            if (store.Kind != ConfigurationStorageKind.JsonFile)
            {
                throw new InvalidOperationException(
                    $"适配器 {adapter.Definition.Id} 的存储 {store.Id} 尚未实现: {store.Kind}");
            }

            var path = ResolveDefinitionPath(store.Location);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"直播伴侣适配器要求的配置文件不存在: {store.Location}", path);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var values = new List<NativeConfigurationValue>();
            foreach (var field in adapter.Definition.Fields
                         .Where(field => string.Equals(field.StoreId, store.Id, StringComparison.Ordinal))
                         .OrderBy(field => field.NativePath, StringComparer.Ordinal))
            {
                if (!TryGetPointer(json.RootElement, field.NativePath, out var value))
                {
                    if (field.Required)
                    {
                        throw new InvalidOperationException(
                            $"直播伴侣配置缺少必需字段 {store.Id}:{field.NativePath}");
                    }

                    continue;
                }

                EnsureExpectedType(field, value);
                values.Add(new NativeConfigurationValue(
                    field.NativePath,
                    Category(field.UnifiedKind),
                    value.Clone()));
            }

            var relativePath = Path.GetRelativePath(RootPath, path);
            var content = JsonSerializer.SerializeToUtf8Bytes(values, JsonOptions);
            documents.Add(new NativeConfigurationDocument(
                store.Id,
                store.Kind.ToString(),
                adapter.Definition.Id,
                store.Location,
                relativePath,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                CreateLogicalId($"live-companion|{store.Id}|{relativePath}"),
                values));
        }

        return documents;
    }

    public static void ValidateDefinedDocuments(
        VerifiedAdapterDefinition adapter,
        IReadOnlyList<NativeConfigurationDocument> documents)
    {
        var fields = adapter.Definition.Fields.ToDictionary(
            field => $"{field.StoreId}\0{field.NativePath}",
            StringComparer.Ordinal);
        var stores = adapter.Definition.Stores.ToDictionary(store => store.Id, StringComparer.Ordinal);
        var capturedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (!stores.TryGetValue(document.StoreId, out var store)
                || !string.Equals(document.StorageKind, store.Kind.ToString(), StringComparison.Ordinal)
                || !string.Equals(document.StructureVersion, adapter.Definition.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"存档包含未声明的配置存储: {document.StoreId}");
            }

            foreach (var value in document.Values)
            {
                var key = $"{document.StoreId}\0{value.JsonPointer}";
                if (!fields.TryGetValue(key, out var field) || !field.Writable)
                {
                    throw new InvalidOperationException(
                        $"存档包含适配器未声明为可写的字段: {document.StoreId}:{value.JsonPointer}");
                }

                EnsureExpectedType(field, value.Value);
                if (!capturedKeys.Add(key))
                {
                    throw new InvalidOperationException($"存档包含重复字段: {document.StoreId}:{value.JsonPointer}");
                }
            }
        }

        var missing = adapter.Definition.Fields.FirstOrDefault(field =>
            field.Required
            && field.Writable
            && !capturedKeys.Contains($"{field.StoreId}\0{field.NativePath}"));
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"存档缺少适配器必需字段: {missing.StoreId}:{missing.NativePath}");
        }
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

    public async Task<LiveCompanionLiveState> InspectDefinedLiveStateAsync(
        VerifiedAdapterDefinition adapter,
        CancellationToken cancellationToken)
    {
        var rule = adapter.Definition.LiveStateRule;
        var store = adapter.Definition.Stores.SingleOrDefault(value =>
            string.Equals(value.Id, rule.StoreId, StringComparison.Ordinal));
        if (store is null || store.Kind != ConfigurationStorageKind.JsonFile)
        {
            return new LiveCompanionLiveState(false, false);
        }

        var path = ResolveDefinitionPath(store.Location);
        if (!File.Exists(path))
        {
            return new LiveCompanionLiveState(false, false);
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
            if (!TryGetPointer(json.RootElement, rule.NativePath, out var value))
            {
                return new LiveCompanionLiveState(false, false);
            }

            var actual = value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.GetRawText();
            return new LiveCompanionLiveState(
                !string.Equals(actual, rule.ExpectedIdleValue, StringComparison.OrdinalIgnoreCase),
                true);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LiveCompanionLiveState(false, false);
        }
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
        IReadOnlyList<AssetBinding> assets,
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
        IReadOnlyList<AssetBinding> assets,
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
        if (!string.Equals(document.StorageKind, ConfigurationStorageKind.JsonFile.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"不支持的直播伴侣存储: {document.StorageKind}");
        }

        var path = Path.GetFullPath(Path.Combine(RootPath, document.RelativePath));
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"直播伴侣配置路径越界: {document.RelativePath}");
        }

        return path;
    }

    private string ResolveDefinitionPath(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || Path.IsPathRooted(location))
        {
            throw new InvalidOperationException("直播伴侣适配定义只能使用配置根目录内的相对路径");
        }

        var path = Path.GetFullPath(Path.Combine(RootPath, location));
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"直播伴侣适配定义路径越界: {location}");
        }

        return path;
    }

    private static bool TryGetPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(UnescapePointer))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property))
            {
                value = property;
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, out var index)
                && index >= 0
                && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            value = default;
            return false;
        }

        return true;
    }

    private static void EnsureExpectedType(FieldMappingDefinition field, JsonElement value)
    {
        var expected = field.ValueType.Trim().ToLowerInvariant();
        var matches = expected switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "int" or "integer" or "number" or "double" => value.ValueKind == JsonValueKind.Number,
            "bool" or "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
        if (!matches)
        {
            throw new InvalidOperationException(
                $"直播伴侣字段类型不匹配 {field.NativePath}: 期望 {field.ValueType}，实际 {value.ValueKind}");
        }
    }

    private static string Category(UnifiedFieldKind field) => field switch
    {
        UnifiedFieldKind.DeviceSelection => NativeParameterCategories.DeviceSelection,
        UnifiedFieldKind.Width or UnifiedFieldKind.Height or UnifiedFieldKind.FramesPerSecond
            or UnifiedFieldKind.PixelFormat or UnifiedFieldKind.ColorSpace or UnifiedFieldKind.ColorRange =>
            NativeParameterCategories.VideoMode,
        _ => NativeParameterCategories.Filter
    };

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

            var targetPath = Path.Combine(assetDirectory, asset.BlobSha256, asset.OriginalFileName);
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
