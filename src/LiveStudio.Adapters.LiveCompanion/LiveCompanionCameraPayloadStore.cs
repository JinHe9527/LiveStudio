using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal sealed class LiveCompanionCameraPayloadStore(string rootPath)
{
    private static readonly string[] RequiredPayloadProperties =
    [
        "deviceId", "format", "width", "height", "rate"
    ];

    public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(
        rootPath,
        "storage",
        "camera-payloads.json"));

    public string SourceStorePath { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(
        rootPath,
        "WBStore",
        "sourceStore.json"));

    public static Task<IReadOnlyList<LiveCompanionCameraTarget>> GetTargetsAsync(
        NativeConfigurationDocument sourceStoreDocument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetTargets(sourceStoreDocument));
    }

    internal static IReadOnlyList<LiveCompanionCameraTarget> GetTargets(
        NativeConfigurationDocument sourceStoreDocument)
    {
        var root = ReconstructSourceStore(sourceStoreDocument);
        var targets = new List<LiveCompanionCameraTarget>();
        if (root["sourceStore"]?["sceneSource"] is not JsonObject scenes)
        {
            return targets;
        }

        foreach (var scene in scenes)
        {
            if (scene.Value is not JsonObject sceneObject
                || sceneObject["data"] is not JsonObject sources)
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (source.Value is not JsonObject sourceObject
                    || !string.Equals(sourceObject["type"]?.GetValue<string>(), "camera", StringComparison.Ordinal)
                    || sourceObject["payload"] is not JsonObject payload)
                {
                    continue;
                }

                var deviceId = payload["deviceId"]?.GetValue<string>();
                var effectConfigurationId = sourceObject["effectConfigId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(effectConfigurationId))
                {
                    throw new InvalidOperationException("存档摄像头缺少设备名称或效果配置标识");
                }

                targets.Add(new LiveCompanionCameraTarget(
                    scene.Key,
                    source.Key,
                    deviceId,
                    effectConfigurationId,
                    payload));
            }
        }

        return targets;
    }

    public async Task<IReadOnlyList<LiveCompanionActiveCamera>> GetActiveCamerasAsync(
        CancellationToken cancellationToken)
    {
        JsonObject root;
        await using (var stream = new FileStream(
                         SourceStorePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         65_536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                   ?? throw new InvalidOperationException("直播伴侣 sourceStore.json 根节点不是对象");
        }

        var cameras = new List<LiveCompanionActiveCamera>();
        if (root["sourceStore"]?["sceneSource"] is not JsonObject scenes)
        {
            return cameras;
        }

        foreach (var scene in scenes)
        {
            if (scene.Value is not JsonObject sceneObject
                || sceneObject["data"] is not JsonObject sources)
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (source.Value is not JsonObject sourceObject
                    || !string.Equals(sourceObject["type"]?.GetValue<string>(), "camera", StringComparison.Ordinal)
                    || sourceObject["payload"] is not JsonObject payload)
                {
                    continue;
                }

                var deviceId = payload["deviceId"]?.GetValue<string>();
                var effectConfigurationId = sourceObject["effectConfigId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(effectConfigurationId))
                {
                    cameras.Add(new LiveCompanionActiveCamera(
                        scene.Key,
                        source.Key,
                        deviceId,
                        effectConfigurationId));
                }
            }
        }

        return cameras;
    }

    public async Task<int> ApplyAsync(
        NativeConfigurationDocument sourceStoreDocument,
        CancellationToken cancellationToken)
    {
        var sourceStoreRoot = ReconstructSourceStore(sourceStoreDocument);
        var payloads = FindCameraPayloads(sourceStoreRoot);
        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("存档中没有可恢复的摄像头设备配置");
        }

        JsonObject target;
        await using (var stream = new FileStream(
                         Path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65_536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            target = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                     ?? throw new InvalidOperationException("直播伴侣 camera-payloads.json 根节点不是对象");
        }

        foreach (var payload in payloads)
        {
            target[payload.Key] = payload.Value.DeepClone();
        }

        await LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(
            Path,
            target,
            cancellationToken);
        return payloads.Count;
    }

    public async Task<int> ApplyToActiveSourcesAsync(
        NativeConfigurationDocument sourceStoreDocument,
        CancellationToken cancellationToken)
    {
        var payloads = FindCameraPayloads(ReconstructSourceStore(sourceStoreDocument));
        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("存档中没有可恢复的摄像头设备配置");
        }

        JsonObject target;
        await using (var stream = new FileStream(
                         SourceStorePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65_536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            target = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                     ?? throw new InvalidOperationException("直播伴侣 sourceStore.json 根节点不是对象");
        }

        var appliedDevices = new HashSet<string>(StringComparer.Ordinal);
        if (target["sourceStore"]?["sceneSource"] is JsonObject sceneSources)
        {
            foreach (var scene in sceneSources)
            {
                if (scene.Value is not JsonObject sceneObject
                    || sceneObject["data"] is not JsonObject sources)
                {
                    continue;
                }

                foreach (var source in sources)
                {
                    if (source.Value is not JsonObject sourceObject
                        || !string.Equals(
                            sourceObject["type"]?.GetValue<string>(),
                            "camera",
                            StringComparison.Ordinal)
                        || sourceObject["payload"] is not JsonObject currentPayload)
                    {
                        continue;
                    }

                    var deviceId = currentPayload["deviceId"]?.GetValue<string>();
                    if (deviceId is not null && payloads.TryGetValue(deviceId, out var expectedPayload))
                    {
                        sourceObject["payload"] = expectedPayload.DeepClone();
                        appliedDevices.Add(deviceId);
                    }
                }
            }
        }

        var missingDevice = payloads.Keys.FirstOrDefault(device => !appliedDevices.Contains(device));
        if (missingDevice is not null)
        {
            throw new InvalidOperationException($"直播伴侣当前场景缺少摄像头设备 {missingDevice}");
        }

        await LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(
            SourceStorePath,
            target,
            cancellationToken);
        return appliedDevices.Count;
    }

    public async Task<int> ApplyPortableAsync(
        NativeConfigurationDocument sourceStoreDocument,
        CancellationToken cancellationToken)
    {
        var payloads = FindCameraPayloads(ReconstructSourceStore(sourceStoreDocument));
        if (payloads.Count != 1)
        {
            throw new InvalidOperationException("可移植存档必须包含且只包含一份摄像头配置");
        }

        JsonObject target;
        await using (var stream = new FileStream(
                         Path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65_536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            target = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                     ?? throw new InvalidOperationException("直播伴侣 camera-payloads.json 根节点不是对象");
        }

        var payload = payloads.Single();
        target[payload.Key] = MergePortablePayload(target[payload.Key], payload.Value);
        await LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(Path, target, cancellationToken);
        return 1;
    }

    public async Task<int> ApplyPortableToActiveSourcesAsync(
        NativeConfigurationDocument sourceStoreDocument,
        CancellationToken cancellationToken)
    {
        var payloads = FindCameraPayloads(ReconstructSourceStore(sourceStoreDocument));
        if (payloads.Count != 1)
        {
            throw new InvalidOperationException("可移植存档必须包含且只包含一份摄像头配置");
        }

        var portable = payloads.Single();
        JsonObject target;
        await using (var stream = new FileStream(
                         SourceStorePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65_536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            target = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject
                     ?? throw new InvalidOperationException("直播伴侣 sourceStore.json 根节点不是对象");
        }

        var applied = 0;
        if (target["sourceStore"]?["sceneSource"] is JsonObject sceneSources)
        {
            foreach (var scene in sceneSources)
            {
                if (scene.Value is not JsonObject sceneObject
                    || sceneObject["data"] is not JsonObject sources)
                {
                    continue;
                }

                foreach (var source in sources)
                {
                    if (source.Value is not JsonObject sourceObject
                        || !string.Equals(sourceObject["type"]?.GetValue<string>(), "camera", StringComparison.Ordinal)
                        || sourceObject["payload"] is not JsonObject currentPayload
                        || !string.Equals(
                            currentPayload["deviceId"]?.GetValue<string>(),
                            portable.Key,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sourceObject["payload"] = MergePortablePayload(currentPayload, portable.Value);
                    applied++;
                }
            }
        }

        if (applied == 0)
        {
            throw new InvalidOperationException($"直播伴侣当前场景缺少摄像头设备 {portable.Key}");
        }

        await LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(
            SourceStorePath,
            target,
            cancellationToken);
        return applied;
    }

    internal static JsonNode? MergePortablePayload(JsonNode? current, JsonNode expected)
    {
        if (expected is JsonObject expectedObject)
        {
            var result = current is JsonObject currentObject
                ? currentObject.DeepClone().AsObject()
                : new JsonObject();
            foreach (var property in expectedObject)
            {
                if (property.Value is not null)
                {
                    result[property.Key] = MergePortablePayload(result[property.Key], property.Value);
                }
                else
                {
                    result[property.Key] = null;
                }
            }

            return result;
        }

        if (expected is JsonArray expectedArray)
        {
            var result = current is JsonArray currentArray
                ? currentArray.DeepClone().AsArray()
                : new JsonArray();
            for (var index = 0; index < expectedArray.Count; index++)
            {
                var expectedItem = expectedArray[index];
                if (index >= result.Count)
                {
                    result.Add(expectedItem?.DeepClone());
                }
                else if (expectedItem is not null)
                {
                    result[index] = MergePortablePayload(result[index], expectedItem);
                }
                else
                {
                    result[index] = null;
                }
            }

            return result;
        }

        return expected.DeepClone();
    }

    internal static JsonObject ReconstructSourceStore(
        NativeConfigurationDocument sourceStoreDocument)
    {
        if (!string.Equals(
                System.IO.Path.GetFileName(sourceStoreDocument.RelativePath),
                "sourceStore.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("摄像头持久化只能由 sourceStore.json 生成");
        }

        var sourceStoreRoot = new JsonObject();
        foreach (var value in sourceStoreDocument.Values)
        {
            LiveCompanionConfigurationStore.SetPointer(
                sourceStoreRoot,
                value.JsonPointer,
                JsonNode.Parse(value.Value.GetRawText()));
        }

        return sourceStoreRoot;
    }

    internal static IReadOnlyDictionary<string, JsonObject> FindCameraPayloads(JsonNode sourceStoreRoot)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (sourceStoreRoot["sourceStore"]?["sceneSource"] is not JsonObject sceneSources)
        {
            return result;
        }

        foreach (var scene in sceneSources)
        {
            if (scene.Value is not JsonObject sceneObject
                || sceneObject["data"] is not JsonObject sources)
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (source.Value is not JsonObject sourceObject
                    || !string.Equals(
                        sourceObject["type"]?.GetValue<string>(),
                        "camera",
                        StringComparison.Ordinal)
                    || sourceObject["payload"] is not JsonObject payload)
                {
                    continue;
                }

                foreach (var propertyName in RequiredPayloadProperties)
                {
                    if (!payload.ContainsKey(propertyName))
                    {
                        throw new InvalidOperationException(
                            $"存档摄像头配置缺少必需字段 {propertyName}");
                    }
                }

                var deviceId = payload["deviceId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    throw new InvalidOperationException("存档摄像头配置缺少设备名称");
                }

                result[deviceId] = payload;
            }
        }

        return result;
    }
}

internal sealed record LiveCompanionCameraTarget(
    string SceneId,
    string SourceId,
    string DeviceId,
    string EffectConfigurationId,
    JsonObject Payload);

internal sealed record LiveCompanionActiveCamera(
    string SceneId,
    string SourceId,
    string DeviceId,
    string EffectConfigurationId);
