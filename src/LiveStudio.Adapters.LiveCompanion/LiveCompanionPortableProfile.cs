using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

/// <summary>
/// 直播伴侣会为每台电脑重新生成场景、来源和效果配置标识。可移植存档只保留一份
/// 实际摄像头画面配置，并在目标电脑上重新绑定这些标识，不能把来源电脑的整棵
/// sourceStore 直接写入目标电脑。
/// </summary>
internal sealed record LiveCompanionPortableProfile(
    NativeConfigurationDocument SourceStoreDocument,
    NativeConfigurationDocument EffectConfigurationDocument,
    LiveCompanionCameraTarget Camera,
    IReadOnlyList<NativeConfigurationDocument> GlobalDocuments)
{
    public const string AdapterId = "webcast-mate-portable-v1";

    private static readonly string[] SensitiveTerms =
    [
        "account", "authorization", "cookie", "credential", "login", "oauth", "password",
        "passport", "secret", "session", "streamkey", "token", "uid"
    ];

    private static readonly string[] RequiredPayloadProperties =
    [
        "deviceId", "format", "width", "height", "rate"
    ];

    public Guid SourceLogicalId => SourceStoreDocument.SourceLogicalId;

    public static LiveCompanionPortableProfile? TryCreate(
        IReadOnlyList<NativeConfigurationDocument> documents) =>
        TryCreate(documents, out _);

    public static LiveCompanionPortableProfile? TryCreate(
        IReadOnlyList<NativeConfigurationDocument> documents,
        out string failureReason)
    {
        failureReason = string.Empty;
        var sourceStore = documents.SingleOrDefault(document => string.Equals(
            Path.GetFileName(document.RelativePath),
            "sourceStore.json",
            StringComparison.OrdinalIgnoreCase));
        var effectConfigurationStore = documents.SingleOrDefault(document => string.Equals(
            Path.GetFileName(document.RelativePath),
            "effectConfigStore.json",
            StringComparison.OrdinalIgnoreCase));
        if (sourceStore is null || effectConfigurationStore is null)
        {
            var missing = new List<string>();
            if (sourceStore is null)
            {
                missing.Add("sourceStore.json");
            }

            if (effectConfigurationStore is null)
            {
                missing.Add("effectConfigStore.json");
            }

            failureReason = $"没有读取到直播伴侣必需存储：{string.Join("、", missing)}";
            return null;
        }

        IReadOnlyList<LiveCompanionCameraTarget> targets;
        try
        {
            targets = LiveCompanionCameraPayloadStore.GetTargets(sourceStore);
        }
        catch (InvalidOperationException exception)
        {
            failureReason = exception.Message;
            return null;
        }

        if (targets.Count == 0)
        {
            failureReason = "sourceStore.json 中没有找到包含设备和视频模式的摄像头来源";
            return null;
        }

        var equivalentGroups = targets.GroupBy(
                target => CreateCameraSignature(target with
                {
                    Payload = CreatePortablePayload(target.Payload)
                }),
                StringComparer.Ordinal)
            .ToArray();
        if (equivalentGroups.Length != 1)
        {
            failureReason = $"检测到 {targets.Count} 个摄像头来源，包含 {equivalentGroups.Length} 套不同画面配置；当前存档无法安全判断应恢复哪一套";
            return null;
        }

        var originalCamera = equivalentGroups[0].First();
        var camera = originalCamera with { Payload = CreatePortablePayload(originalCamera.Payload) };
        var missingProperties = RequiredPayloadProperties
            .Where(property => !camera.Payload.ContainsKey(property))
            .ToArray();
        if (missingProperties.Length > 0)
        {
            failureReason = $"摄像头配置缺少设备或视频模式字段：{string.Join("、", missingProperties)}";
            return null;
        }

        var sourcePrefix = CameraPrefix(camera);
        var sourceValues = sourceStore.Values
            .Where(value => value.JsonPointer.StartsWith(sourcePrefix + "/", StringComparison.Ordinal))
            .Where(value => !IsSourceRuntimeIdentifier(value.JsonPointer, sourcePrefix))
            .Select(value => string.Equals(
                    value.JsonPointer,
                    sourcePrefix + "/payload/deviceId",
                    StringComparison.Ordinal)
                ? value with { Category = NativeParameterCategories.DeviceSelection }
                : value)
            .ToArray();
        var effectPrefix = $"/effectConfigStore/configs/{EscapePointer(camera.EffectConfigurationId)}";
        var effectValues = effectConfigurationStore.Values
            .Where(value => value.JsonPointer.StartsWith(effectPrefix + "/", StringComparison.Ordinal))
            .ToArray();
        if (sourceValues.Length == 0
            || effectValues.Length == 0)
        {
            failureReason = sourceValues.Length == 0
                ? "摄像头来源没有可保存的画面参数"
                : "摄像头没有对应的美颜、滤镜效果配置";
            return null;
        }

        if (sourceValues.Concat(effectValues).Any(value => ContainsSensitiveTerm(value.JsonPointer)))
        {
            failureReason = "摄像头画面配置中混入账号或登录敏感字段，已拒绝生成存档";
            return null;
        }

        var portableSource = WithValues(sourceStore, sourceValues);
        var portableEffect = WithValues(effectConfigurationStore, effectValues);
        try
        {
            _ = LiveCompanionCameraPayloadStore.FindCameraPayloads(
                LiveCompanionCameraPayloadStore.ReconstructSourceStore(portableSource));
            _ = ReconstructEffectConfiguration(portableEffect, camera.EffectConfigurationId);
        }
        catch (InvalidOperationException exception)
        {
            failureReason = $"摄像头画面配置无法重建：{exception.Message}";
            return null;
        }

        var identifierAliases = equivalentGroups[0]
            .SelectMany(target => new[]
            {
                new KeyValuePair<string, string>(target.SceneId, camera.SceneId),
                new KeyValuePair<string, string>(target.SourceId, camera.SourceId),
                new KeyValuePair<string, string>(
                    target.EffectConfigurationId,
                    camera.EffectConfigurationId)
            })
            .Where(pair => !string.Equals(pair.Key, pair.Value, StringComparison.Ordinal))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        var globals = documents.Where(document =>
                string.Equals(
                    Path.GetFileName(document.RelativePath),
                    "effectStore.json",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(document.RelativePath),
                    "filterStore.json",
                    StringComparison.OrdinalIgnoreCase))
            .Select(document => RebindDocumentIdentifiers(document, identifierAliases))
            .ToArray();
        return new LiveCompanionPortableProfile(portableSource, portableEffect, camera, globals);
    }

    public VideoSource CreateVideoSource()
    {
        var payload = Camera.Payload;
        var deviceId = Camera.DeviceId;
        var friendlyName = ReadString(payload, "name") ?? deviceId;
        var mode = new VideoMode(
            ReadInt(payload, "width"),
            ReadInt(payload, "height"),
            ReadInt(payload, "rate"),
            1,
            ReadStringOrNumber(payload, "format"),
            ReadStringOrNumber(payload, "colorSpace"),
            ReadStringOrNumber(payload, "videoRange"));
        var settings = payload.ToDictionary(
            property => property.Key,
            property => JsonSerializer.SerializeToElement(property.Value),
            StringComparer.Ordinal);
        return new VideoSource(
            SourceLogicalId,
            friendlyName,
            "camera",
            new CaptureDeviceDescriptor(
                friendlyName,
                null,
                null,
                null,
                deviceId,
                [mode]),
            mode,
            settings,
            [],
            "camera");
    }

    public IReadOnlyList<NativeConfigurationDocument> CreateExpectedDocuments(
        VerifiedAdapterDefinition adapter,
        IReadOnlyDictionary<Guid, DeviceMapping> mappings,
        IReadOnlyList<AssetBinding> assets,
        string assetDirectory)
    {
        var documents = new List<NativeConfigurationDocument>
        {
            SourceStoreDocument,
            EffectConfigurationDocument
        };
        documents.AddRange(CreateSignedGlobalDocuments(adapter));
        return LiveCompanionConfigurationStore.CreateExpectedDocuments(
            documents,
            mappings,
            assets,
            assetDirectory);
    }

    public IReadOnlyList<CapturedParameterField> CreateFieldCoverage() =>
        new[] { SourceStoreDocument, EffectConfigurationDocument }
            .SelectMany(document => document.Values.Select(value => new CapturedParameterField(
                $"{document.RelativePath}:{value.JsonPointer}",
                value.Category,
                value.Value.ValueKind.ToString(),
                true,
                true,
                "PortableNativeImportReadback",
                $"portable:{document.SourceLogicalId:N}:{value.JsonPointer}",
                value.JsonPointer.Split('/').LastOrDefault() ?? value.JsonPointer,
                "可移植画面配置",
                FieldEvidenceStatus.Mapped)))
            .ToArray();

    public string ResolveTargetDeviceId(IReadOnlyDictionary<Guid, DeviceMapping> mappings) =>
        mappings.TryGetValue(SourceLogicalId, out var mapping)
            ? mapping.TargetDeviceId
            : Camera.DeviceId;

    public static bool CanRestoreTo(
        LiveCompanionAdapterDefinition definition,
        IReadOnlyList<NativeConfigurationDocument> targetDocuments)
    {
        var locations = targetDocuments
            .Select(document => NormalizeLocation(document.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (definition.Stores.Any(store => !locations.Contains(NormalizeLocation(store.Location))))
        {
            return false;
        }

        var sourceStore = targetDocuments.SingleOrDefault(document => string.Equals(
            Path.GetFileName(document.RelativePath),
            "sourceStore.json",
            StringComparison.OrdinalIgnoreCase));
        return sourceStore is not null
               && sourceStore.Values.Any(value => value.JsonPointer.StartsWith(
                   "/sourceStore/sceneSource/",
                   StringComparison.Ordinal));
    }

    private static JsonNode ReconstructEffectConfiguration(
        NativeConfigurationDocument document,
        string effectConfigurationId)
    {
        var root = new JsonObject();
        foreach (var value in document.Values)
        {
            LiveCompanionConfigurationStore.SetPointer(
                root,
                value.JsonPointer,
                JsonNode.Parse(value.Value.GetRawText()));
        }

        return root["effectConfigStore"]?["configs"]?[effectConfigurationId]
               ?? throw new InvalidOperationException("存档缺少摄像头引用的效果配置");
    }

    private List<NativeConfigurationDocument> CreateSignedGlobalDocuments(
        VerifiedAdapterDefinition adapter)
    {
        var stores = adapter.Definition.Stores.ToDictionary(store => store.Id, StringComparer.Ordinal);
        var canonicalCamera = adapter.Definition.Fields
            .Where(field => string.Equals(field.StoreId, "source-store", StringComparison.Ordinal))
            .Select(field => PointerSegments(field.NativePath))
            .FirstOrDefault(segments => segments.Length >= 5
                                        && string.Equals(segments[0], "sourceStore", StringComparison.Ordinal)
                                        && string.Equals(segments[1], "sceneSource", StringComparison.Ordinal)
                                        && string.Equals(segments[3], "data", StringComparison.Ordinal));
        var canonicalEffectId = adapter.Definition.Fields
            .Where(field => string.Equals(field.StoreId, "effect-config", StringComparison.Ordinal))
            .Select(field => PointerSegments(field.NativePath))
            .FirstOrDefault(segments => segments.Length >= 4
                                        && string.Equals(segments[0], "effectConfigStore", StringComparison.Ordinal)
                                        && string.Equals(segments[1], "configs", StringComparison.Ordinal))?
            .ElementAtOrDefault(2);
        if (canonicalCamera is null || string.IsNullOrWhiteSpace(canonicalEffectId))
        {
            return [];
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [canonicalCamera[2]] = Camera.SceneId,
            [canonicalCamera[4]] = Camera.SourceId,
            [canonicalEffectId] = Camera.EffectConfigurationId
        };
        var result = new List<NativeConfigurationDocument>();
        foreach (var global in GlobalDocuments)
        {
            var store = adapter.Definition.Stores.SingleOrDefault(candidate => string.Equals(
                NormalizeLocation(candidate.Location),
                NormalizeLocation(global.RelativePath),
                StringComparison.OrdinalIgnoreCase));
            if (store is null || !stores.ContainsKey(store.Id))
            {
                continue;
            }

            var available = global.Values.ToDictionary(value => value.JsonPointer, StringComparer.Ordinal);
            var values = adapter.Definition.Fields
                .Where(field => string.Equals(field.StoreId, store.Id, StringComparison.Ordinal)
                                && LiveCompanionConfigurationStore.IsRestorableField(field))
                .Select(field => TranslatePointer(field.NativePath, replacements))
                .Where(available.ContainsKey)
                .Select(path => available[path])
                .ToArray();
            if (values.Length > 0)
            {
                result.Add(WithValues(global with
                {
                    StoreId = store.Id,
                    StructureVersion = adapter.Definition.Id
                }, values));
            }
        }

        return result;
    }

    private static NativeConfigurationDocument WithValues(
        NativeConfigurationDocument document,
        IReadOnlyList<NativeConfigurationValue> values)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(values);
        return document with
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Values = values
        };
    }

    private static NativeConfigurationDocument RebindDocumentIdentifiers(
        NativeConfigurationDocument document,
        Dictionary<string, string> replacements)
    {
        if (replacements.Count == 0)
        {
            return document;
        }

        var values = document.Values.Select(value => value with
        {
            JsonPointer = TranslatePointer(value.JsonPointer, replacements),
            Value = JsonSerializer.SerializeToElement(
                ReplaceIdentifiers(JsonNode.Parse(value.Value.GetRawText()), replacements))
        }).ToArray();
        return WithValues(document, values);
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

    private static string CreateCameraSignature(LiveCompanionCameraTarget camera)
    {
        var content = $"{camera.DeviceId}\0{camera.EffectConfigurationId}\0{camera.Payload.ToJsonString()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static string CameraPrefix(LiveCompanionCameraTarget camera) =>
        $"/sourceStore/sceneSource/{EscapePointer(camera.SceneId)}/data/{EscapePointer(camera.SourceId)}";

    private static JsonObject CreatePortablePayload(JsonObject payload)
    {
        var result = payload.DeepClone().AsObject();
        if (result["filterData"]?["filterDataList"] is JsonArray filters)
        {
            foreach (var filter in filters.OfType<JsonObject>())
            {
                filter.Remove("id");
            }
        }

        return result;
    }

    private static bool IsSourceRuntimeIdentifier(string pointer, string sourcePrefix)
    {
        var relative = pointer[sourcePrefix.Length..];
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 5
               && string.Equals(segments[0], "payload", StringComparison.Ordinal)
               && string.Equals(segments[1], "filterData", StringComparison.Ordinal)
               && string.Equals(segments[2], "filterDataList", StringComparison.Ordinal)
               && int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)
               && string.Equals(segments[4], "id", StringComparison.Ordinal);
    }

    private static int ReadInt(JsonObject payload, string propertyName)
    {
        var node = payload[propertyName];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var integer))
            {
                return integer;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return checked((int)Math.Round(number));
            }

            if (value.TryGetValue<string>(out var text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                return integer;
            }
        }

        return 0;
    }

    private static string? ReadString(JsonObject payload, string propertyName) =>
        payload[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string ReadStringOrNumber(JsonObject payload, string propertyName)
    {
        var node = payload[propertyName];
        return node is null
            ? string.Empty
            : node is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : node.ToJsonString();
    }

    private static bool ContainsSensitiveTerm(string pointer)
    {
        var normalized = string.Concat(pointer.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return SensitiveTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private static string NormalizeLocation(string location) => location
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        .TrimStart(Path.DirectorySeparatorChar);

    private static string TranslatePointer(
        string pointer,
        Dictionary<string, string> replacements) => "/" + string.Join(
        '/',
        PointerSegments(pointer).Select(segment => EscapePointer(
            replacements.TryGetValue(segment, out var replacement)
                ? replacement
                : segment)));

    private static string[] PointerSegments(string pointer) => pointer
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => segment
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal))
        .ToArray();

    private static string EscapePointer(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
