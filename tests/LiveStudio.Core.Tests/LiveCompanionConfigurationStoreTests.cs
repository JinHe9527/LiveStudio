using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionConfigurationStoreTests
{
    [Fact]
    public void PortableProfileCollapsesDuplicateSceneInstancesAndMapsTargetDevice()
    {
        var sourceLogicalId = Guid.NewGuid();
        var firstPrefix = "/sourceStore/sceneSource/source-machine-scene-a/data/source-machine-camera-a";
        var secondPrefix = "/sourceStore/sceneSource/source-machine-scene-b/data/source-machine-camera-b";
        var source = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\sourceStore.json",
            @"WBStore\sourceStore.json",
            "hash",
            sourceLogicalId,
            CameraValues(firstPrefix, "source-effect", "source-device")
                .Concat(CameraValues(secondPrefix, "source-effect", "source-device"))
                .OrderBy(value => value.JsonPointer, StringComparer.Ordinal)
                .ToArray());
        var effect = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\effectConfigStore.json",
            @"WBStore\effectConfigStore.json",
            "hash",
            Guid.NewGuid(),
            [
                Value("/effectConfigStore/configs/source-effect/effect/items/source-item/sliders/BigEye/absVal", 0.42),
                Value("/effectConfigStore/configs/source-effect/effect/items/source-item/sliders/BigEye/use", true),
                Value("/effectConfigStore/configs/source-effect/effect/items/source-item/sliders/BigEye/value", 0.42)
            ]);
        var global = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\effectStore.json",
            @"WBStore\effectStore.json",
            "hash",
            Guid.NewGuid(),
            [
                Value("/effectStore/useCurveFilter", true),
                // 运行时全局引用可能指向同一画面配置的另一个场景实例。可移植投影
                // 必须先折叠为代表来源，之后才能在目标电脑上重新绑定。
                Value("/effectStore/interactSourceId", "source-machine-camera-b")
            ]);

        var profile = Assert.IsType<LiveCompanionPortableProfile>(
            LiveCompanionPortableProfile.TryCreate([source, effect, global]));
        var videoSource = profile.CreateVideoSource();
        Assert.Equal(sourceLogicalId, videoSource.LogicalId);
        Assert.Equal("source-device", videoSource.Device?.InterfaceHint);
        Assert.Equal(1920, videoSource.Mode?.Width);
        Assert.Equal(60, videoSource.Mode?.FramesPerSecondNumerator);
        var fractionalSource = source with
        {
            Values = source.Values.Select(value => value.JsonPointer.EndsWith("/rate", StringComparison.Ordinal)
                ? value with { Value = JsonSerializer.SerializeToElement(29.97m) } : value).ToArray()
        };
        var fractionalMode = LiveCompanionPortableProfile.TryCreate([fractionalSource, effect, global])!.CreateVideoSource().Mode;
        Assert.Equal(2997, fractionalMode?.FramesPerSecondNumerator);
        Assert.Equal(100, fractionalMode?.FramesPerSecondDenominator);
        Assert.Null(profile.FindTargetTypeMismatch([source, effect, global]));
        var incompatibleEffect = effect with
        {
            Values = effect.Values.Select(value => value.JsonPointer.EndsWith("/absVal", StringComparison.Ordinal)
                ? value with { Value = JsonSerializer.SerializeToElement("wrong-type") } : value).ToArray()
        };
        Assert.EndsWith("/absVal", profile.FindTargetTypeMismatch([source, incompatibleEffect, global]), StringComparison.Ordinal);
        var toggledEffect = effect with
        {
            Values = effect.Values.Select(value => value.Value.ValueKind == JsonValueKind.True
                ? value with { Value = JsonSerializer.SerializeToElement(false) } : value).ToArray()
        };
        Assert.Null(profile.FindTargetTypeMismatch([source, toggledEffect, global]));

        var mapping = new DeviceMapping(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            sourceLogicalId,
            ApplicationKind.LiveCompanion,
            "target-device",
            "目标摄像头",
            string.Empty,
            false);
        var expected = profile.CreateExpectedDocuments(
            CreatePortableAdapter(),
            new Dictionary<Guid, DeviceMapping> { [sourceLogicalId] = mapping },
            [],
            Path.GetTempPath());
        var expectedSource = Assert.Single(expected, document =>
            string.Equals(Path.GetFileName(document.RelativePath), "sourceStore.json", StringComparison.Ordinal));
        var targets = LiveCompanionCameraPayloadStore.GetTargets(expectedSource);
        var target = Assert.Single(targets);
        Assert.Equal("target-device", target.DeviceId);
        Assert.Equal("source-effect", target.EffectConfigurationId);
        Assert.Single(expected, document =>
            string.Equals(Path.GetFileName(document.RelativePath), "effectConfigStore.json", StringComparison.Ordinal));
        var expectedGlobal = Assert.Single(expected, document =>
            string.Equals(Path.GetFileName(document.RelativePath), "effectStore.json", StringComparison.Ordinal));
        Assert.Contains(expectedGlobal.Values, value =>
            value.JsonPointer == "/effectStore/useCurveFilter");
        Assert.Contains(expectedGlobal.Values, value =>
            value.JsonPointer == "/effectStore/interactSourceId"
            && value.Value.GetString() == "source-machine-camera-a");
    }

    private static VerifiedAdapterDefinition CreatePortableAdapter()
    {
        var definition = new LiveCompanionAdapterDefinition(
            "portable-test",
            "1.0.0",
            "1.0.0",
            new string('a', 64),
            [
                new ConfigurationStoreDefinition("source-store", ConfigurationStorageKind.JsonFile, @"WBStore\sourceStore.json", null, true),
                new ConfigurationStoreDefinition("effect-config", ConfigurationStorageKind.JsonFile, @"WBStore\effectConfigStore.json", null, true),
                new ConfigurationStoreDefinition("effect-store", ConfigurationStorageKind.JsonFile, @"WBStore\effectStore.json", null, true),
                new ConfigurationStoreDefinition("filter-store", ConfigurationStorageKind.JsonFile, @"WBStore\filterStore.json", null, true)
            ],
            [
                new FieldMappingDefinition("device", UnifiedFieldKind.DeviceSelection, "source-store", "/sourceStore/sceneSource/canonical-scene/data/canonical-source/payload/deviceId", "string", true, true),
                new FieldMappingDefinition("effect", UnifiedFieldKind.FilterSetting, "effect-config", "/effectConfigStore/configs/canonical-effect/effect/value", "number", true, true),
                new FieldMappingDefinition("global-static", UnifiedFieldKind.FilterSetting, "effect-store", "/effectStore/useCurveFilter", "bool", true, true),
                new FieldMappingDefinition("global-source", UnifiedFieldKind.FilterSetting, "effect-store", "/effectStore/interactSourceId", "string", true, true)
            ],
            [],
            new LiveStateRuleDefinition("source-store", "/idle", "false"),
            new ScreenshotRuleDefinition("window", "main"));
        return new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
    }

    [Fact]
    public void PortableProfileRejectsAmbiguousCameraSettings()
    {
        var firstPrefix = "/sourceStore/sceneSource/scene-a/data/camera-a";
        var secondPrefix = "/sourceStore/sceneSource/scene-b/data/camera-b";
        var first = CameraValues(firstPrefix, "effect-a", "device-a");
        var second = CameraValues(secondPrefix, "effect-b", "device-b");
        var source = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\sourceStore.json",
            @"WBStore\sourceStore.json",
            "hash",
            Guid.NewGuid(),
            first.Concat(second).ToArray());
        var effect = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\effectConfigStore.json",
            @"WBStore\effectConfigStore.json",
            "hash",
            Guid.NewGuid(),
            [
                Value("/effectConfigStore/configs/effect-a/effect/items/1/value", 1),
                Value("/effectConfigStore/configs/effect-b/effect/items/1/value", 2)
            ]);

        Assert.Null(LiveCompanionPortableProfile.TryCreate([source, effect], out var reason));
        Assert.Contains("2 套不同画面配置", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableProfileAcceptsVersionsWithoutOptionalCameraProperties()
    {
        const string prefix = "/sourceStore/sceneSource/scene-a/data/camera-a";
        var source = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\sourceStore.json",
            @"WBStore\sourceStore.json",
            "hash",
            Guid.NewGuid(),
            [
                Value($"{prefix}/type", "camera"),
                Value($"{prefix}/effectConfigId", "effect-a"),
                Value($"{prefix}/payload/deviceId", "device-a"),
                Value($"{prefix}/payload/format", 6),
                Value($"{prefix}/payload/width", 1920),
                Value($"{prefix}/payload/height", 1080),
                Value($"{prefix}/payload/rate", 60)
            ]);
        var effect = new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            @"WBStore\effectConfigStore.json",
            @"WBStore\effectConfigStore.json",
            "hash",
            Guid.NewGuid(),
            [Value("/effectConfigStore/configs/effect-a/effect/items/1/value", 1)]);

        var profile = Assert.IsType<LiveCompanionPortableProfile>(
            LiveCompanionPortableProfile.TryCreate([source, effect], out var reason));

        Assert.Empty(reason);
        Assert.Equal("device-a", profile.Camera.DeviceId);
        Assert.Equal(string.Empty, profile.CreateVideoSource().Mode?.ColorSpace);
    }

    [Fact]
    public void PortablePayloadMergePreservesTargetRuntimeIdentifiers()
    {
        var current = JsonNode.Parse(
            """
            {
              "deviceId": "target-device",
              "targetOnly": "keep",
              "filterData": {
                "filterDataList": [
                  { "id": "target-filter-id", "name": "HSL", "payload": { "red": { "hueAdjust": 0.01 } } }
                ]
              }
            }
            """)!;
        var expected = JsonNode.Parse(
            """
            {
              "deviceId": "target-device",
              "filterData": {
                "filterDataList": [
                  { "name": "HSL", "payload": { "red": { "hueAdjust": 0.42 } } }
                ]
              }
            }
            """)!;

        var merged = LiveCompanionCameraPayloadStore.MergePortablePayload(current, expected)!.AsObject();

        Assert.Equal("keep", merged["targetOnly"]!.GetValue<string>());
        var filter = merged["filterData"]!["filterDataList"]![0]!;
        Assert.Equal("target-filter-id", filter["id"]!.GetValue<string>());
        Assert.Equal(0.42, filter["payload"]!["red"]!["hueAdjust"]!.GetValue<double>());
    }

    [Fact]
    public async Task PortableGlobalsRebindSourceIdentifiersWithoutReplacingTargetTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-portable-global-{Guid.NewGuid():N}");
        var wbStore = Path.Combine(root, "WBStore");
        Directory.CreateDirectory(wbStore);
        var path = Path.Combine(wbStore, "effectStore.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "effectStore": {
                "useCurveFilter": false,
                "interactSourceId": "old-target-source",
                "targetOnly": "keep"
              }
            }
            """);
        try
        {
            var document = new NativeConfigurationDocument(
                "effect-store",
                "JsonFile",
                "portable-test",
                @"WBStore\effectStore.json",
                @"WBStore\effectStore.json",
                "hash",
                Guid.NewGuid(),
                [
                    Value("/effectStore/useCurveFilter", true),
                    Value("/effectStore/interactSourceId", "source-machine-camera")
                ]);
            var source = new LiveCompanionCameraTarget(
                "source-machine-scene",
                "source-machine-camera",
                "source-device",
                "source-machine-effect",
                new JsonObject());
            var target = new LiveCompanionActiveCamera(
                "target-scene",
                "target-camera",
                "target-device",
                "target-effect");

            var store = new LiveCompanionConfigurationStore(root);
            await store.ApplyPortableBoundDocumentsAsync([document], source, [target], CancellationToken.None);

            using var applied = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var effectStore = applied.RootElement.GetProperty("effectStore");
            Assert.True(effectStore.GetProperty("useCurveFilter").GetBoolean());
            Assert.Equal("target-camera", effectStore.GetProperty("interactSourceId").GetString());
            Assert.Equal("keep", effectStore.GetProperty("targetOnly").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PortableEffectConfigurationRebindsToTargetEffectWithoutReplacingTargetTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-portable-effect-{Guid.NewGuid():N}");
        var wbStore = Path.Combine(root, "WBStore");
        Directory.CreateDirectory(wbStore);
        var path = Path.Combine(wbStore, "effectConfigStore.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "effectConfigStore": {
                "configs": {
                  "target-effect": {
                    "targetOnly": "keep",
                    "beauty": { "smooth": 0 }
                  }
                }
              }
            }
            """);
        try
        {
            var document = new NativeConfigurationDocument(
                "effect-config",
                "JsonFile",
                "portable-test",
                @"WBStore\effectConfigStore.json",
                @"WBStore\effectConfigStore.json",
                "hash",
                Guid.NewGuid(),
                [
                    Value(
                        "/effectConfigStore/configs/source-effect/beauty/smooth",
                        0.73)
                ]);
            var source = new LiveCompanionCameraTarget(
                "source-scene",
                "source-camera",
                "source-device",
                "source-effect",
                new JsonObject());
            var target = new LiveCompanionActiveCamera(
                "target-scene",
                "target-camera",
                "target-device",
                "target-effect");

            var store = new LiveCompanionConfigurationStore(root);
            await store.ApplyPortableBoundDocumentsAsync(
                [document],
                source,
                [target],
                CancellationToken.None);

            using var applied = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var targetConfig = applied.RootElement
                .GetProperty("effectConfigStore")
                .GetProperty("configs")
                .GetProperty("target-effect");
            Assert.Equal(0.73, targetConfig.GetProperty("beauty").GetProperty("smooth").GetDouble());
            Assert.Equal("keep", targetConfig.GetProperty("targetOnly").GetString());
            Assert.False(applied.RootElement
                .GetProperty("effectConfigStore")
                .GetProperty("configs")
                .TryGetProperty("source-effect", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<NativeConfigurationValue> CameraValues(
        string prefix,
        string effectConfigurationId,
        string deviceId) =>
    [
        Value($"{prefix}/type", "camera"),
        Value($"{prefix}/effectConfigId", effectConfigurationId),
        Value($"{prefix}/payload/deviceId", deviceId),
        Value($"{prefix}/payload/name", deviceId),
        Value($"{prefix}/payload/format", 6),
        Value($"{prefix}/payload/width", 1920),
        Value($"{prefix}/payload/height", 1080),
        Value($"{prefix}/payload/rate", 60),
        Value($"{prefix}/payload/effect1/useLoki", true),
        Value($"{prefix}/payload/videoRange", 2),
        Value($"{prefix}/payload/colorSpace", 2),
        Value($"{prefix}/payload/filterData/enable", true),
        Value($"{prefix}/payload/filterData/filterDataList/0/name", "HSL"),
        Value($"{prefix}/payload/filterData/filterDataList/0/payload/red/hueAdjust", 0.04)
    ];

    [Fact]
    public async Task RestoresAuthoritativeCameraPayloadFromCapturedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-camera-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var storage = Path.Combine(root, "storage");
            Directory.CreateDirectory(storage);
            await File.WriteAllTextAsync(
                Path.Combine(storage, "camera-payloads.json"),
                """
                {
                  "Other Camera": { "deviceId": "Other Camera" },
                  "OBS Virtual Camera": { "deviceId": "OBS Virtual Camera", "filterData": { "filterDataList": [] } }
                }
                """);
            var wbStore = Path.Combine(root, "WBStore");
            Directory.CreateDirectory(wbStore);
            await File.WriteAllTextAsync(
                Path.Combine(wbStore, "sourceStore.json"),
                """
                {
                  "sourceStore": {
                    "sceneSource": {
                      "scene4": {
                        "data": {
                          "new-runtime-id": {
                            "type": "camera",
                            "effectConfigId": "keep-runtime-effect-id",
                            "payload": {
                              "deviceId": "OBS Virtual Camera",
                              "filterData": { "filterDataList": [] }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """);
            const string prefix = "/sourceStore/sceneSource/scene4/data/source-1";
            var document = new NativeConfigurationDocument(
                "source-store",
                "JsonFile",
                "test",
                @"WBStore\sourceStore.json",
                @"WBStore\sourceStore.json",
                "hash",
                Guid.NewGuid(),
                [
                    Value($"{prefix}/type", "camera"),
                    Value($"{prefix}/payload/deviceId", "OBS Virtual Camera"),
                    Value($"{prefix}/payload/name", "OBS Virtual Camera"),
                    Value($"{prefix}/payload/format", 6),
                    Value($"{prefix}/payload/width", 1920),
                    Value($"{prefix}/payload/height", 1080),
                    Value($"{prefix}/payload/rate", 30),
                    Value($"{prefix}/payload/effect1/useLoki", true),
                    Value($"{prefix}/payload/videoRange", 2),
                    Value($"{prefix}/payload/colorSpace", 2),
                    Value($"{prefix}/payload/filterData/enable", true),
                    Value($"{prefix}/payload/filterData/filterDataList/0/name", "HSL"),
                    Value($"{prefix}/payload/filterData/filterDataList/0/payload/red/hueAdjust", 0.04)
                ]);

            var store = new LiveCompanionCameraPayloadStore(root);
            var restored = await store.ApplyAsync(document, CancellationToken.None);
            var reconciled = await store.ApplyToActiveSourcesAsync(document, CancellationToken.None);

            Assert.Equal(1, restored);
            Assert.Equal(1, reconciled);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.Path));
            Assert.True(json.RootElement.TryGetProperty("Other Camera", out _));
            var camera = json.RootElement.GetProperty("OBS Virtual Camera");
            Assert.Equal(6, camera.GetProperty("format").GetInt32());
            Assert.Equal(
                0.04,
                camera.GetProperty("filterData")
                    .GetProperty("filterDataList")[0]
                    .GetProperty("payload")
                    .GetProperty("red")
                    .GetProperty("hueAdjust")
                    .GetDouble());
            using var sourceJson = JsonDocument.Parse(await File.ReadAllTextAsync(store.SourceStorePath));
            var source = sourceJson.RootElement
                .GetProperty("sourceStore")
                .GetProperty("sceneSource")
                .GetProperty("scene4")
                .GetProperty("data")
                .GetProperty("new-runtime-id");
            Assert.Equal("keep-runtime-effect-id", source.GetProperty("effectConfigId").GetString());
            Assert.Equal(
                0.04,
                source.GetProperty("payload")
                    .GetProperty("filterData")
                    .GetProperty("filterDataList")[0]
                    .GetProperty("payload")
                    .GetProperty("red")
                    .GetProperty("hueAdjust")
                    .GetDouble());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NativeConfigurationValue Value<T>(string path, T value) =>
        new(path, "Discovery", JsonSerializer.SerializeToElement(value));

    private const string CameraSourcePointer = "/sourceStore/sceneSource/scene4/data/source-1";

    [Fact]
    public async Task CapturesAllowedNativeTreeWithoutCredentialsOrGuessedSemantics()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);

            var documents = await store.CaptureDocumentsAsync(CancellationToken.None);
            var serialized = JsonSerializer.Serialize(documents);

            var document = Assert.Single(documents);
            Assert.DoesNotContain("secret-token", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("embedded-token", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("login", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.All(document.Values, value => Assert.Equal(NativeParameterCategories.Unmapped, value.Category));
            Assert.Equal("capture-card-a", Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/deviceId").Value.GetString());
            Assert.Equal(1920, Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/width").Value.GetInt32());
            Assert.Equal("人像调色", Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/filterChain/0/name").Value.GetString());
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PreservesNativeStringNumberTypesDuringDiscovery()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var content = OriginalConfiguration(fixture.LutPath)
                .Replace("\"width\": 1920", "\"width\": \"1920\"", StringComparison.Ordinal)
                .Replace("\"height\": 1080", "\"height\": \"1080\"", StringComparison.Ordinal)
                .Replace("\"rate\": 60", "\"rate\": \"60\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixture.ConfigurationPath, content);
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);

            var documents = await store.CaptureDocumentsAsync(CancellationToken.None);
            var document = Assert.Single(documents);
            var width = Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/width").Value;
            Assert.Equal(JsonValueKind.String, width.ValueKind);
            Assert.Equal("1920", width.GetString());
            Assert.Equal("1080", Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/height").Value.GetString());
            Assert.Equal("60", Assert.Single(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/rate").Value.GetString());
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task AppliesMappedDeviceFilterAssetsAndRestoresOriginalFileBytes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var discovered = Assert.Single(await store.CaptureDocumentsAsync(CancellationToken.None));
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath));
            var source = original.RootElement.GetProperty("sourceStore")
                .GetProperty("sceneSource")
                .GetProperty("scene4")
                .GetProperty("data")
                .GetProperty("source-1");
            var payload = source.GetProperty("payload");
            var document = discovered with
            {
                Values =
                [
                    new NativeConfigurationValue(
                        $"{CameraSourcePointer}/payload/deviceId",
                        NativeParameterCategories.DeviceSelection,
                        payload.GetProperty("deviceId").Clone()),
                    new NativeConfigurationValue(
                        $"{CameraSourcePointer}/payload/width",
                        NativeParameterCategories.VideoMode,
                        payload.GetProperty("width").Clone()),
                    new NativeConfigurationValue(
                        $"{CameraSourcePointer}/payload/rate",
                        NativeParameterCategories.VideoMode,
                        payload.GetProperty("rate").Clone()),
                    new NativeConfigurationValue(
                        $"{CameraSourcePointer}/payload/filterChain",
                        NativeParameterCategories.Filter,
                        payload.GetProperty("filterChain").Clone())
                ]
            };
            var assetBytes = await File.ReadAllBytesAsync(fixture.LutPath);
            var assetHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(assetBytes));
            AssetBinding[] assets =
            [
                new(
                    Guid.NewGuid(),
                    assetHash,
                    Path.GetFileName(fixture.LutPath),
                    fixture.LutPath,
                    $"{CameraSourcePointer}/payload/filterChain/0/imagePath",
                    assetBytes.LongLength)
            ];
            var assetDirectory = Path.Combine(fixture.RootPath, "stable-assets");
            foreach (var asset in assets)
            {
                var directory = Path.Combine(assetDirectory, asset.BlobSha256);
                Directory.CreateDirectory(directory);
                File.Copy(asset.SourcePath, Path.Combine(directory, asset.OriginalFileName));
            }

            var backup = await store.BackupAsync([document], CancellationToken.None);
            await File.WriteAllTextAsync(fixture.ConfigurationPath, ChangedConfiguration(fixture.LutPath));
            var mapping = new DeviceMapping(
                Guid.NewGuid(),
                Guid.Empty,
                Guid.Empty,
                document.SourceLogicalId,
                ApplicationKind.LiveCompanion,
                "capture-card-b",
                "摄像头",
                string.Empty,
                false);

            await store.ApplyAsync(
                [document],
                new Dictionary<Guid, DeviceMapping> { [document.SourceLogicalId] = mapping },
                assets,
                assetDirectory,
                CancellationToken.None);

            using (var applied = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath)))
            {
                var appliedPayload = applied.RootElement.GetProperty("sourceStore")
                    .GetProperty("sceneSource")
                    .GetProperty("scene4")
                    .GetProperty("data")
                    .GetProperty("source-1")
                    .GetProperty("payload");
                Assert.Equal("capture-card-b", appliedPayload.GetProperty("deviceId").GetString());
                Assert.Equal(1920, appliedPayload.GetProperty("width").GetInt32());
                Assert.Equal(60, appliedPayload.GetProperty("rate").GetInt32());
                var filter = appliedPayload.GetProperty("filterChain")[0];
                Assert.Equal(0.12, filter.GetProperty("contrast").GetDouble());
                Assert.StartsWith(assetDirectory, filter.GetProperty("imagePath").GetString(), StringComparison.Ordinal);
                Assert.Equal("keep-me", applied.RootElement.GetProperty("unrelated").GetString());
                Assert.Equal("secret-token", applied.RootElement.GetProperty("login").GetProperty("token").GetString());
            }

            await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, CancellationToken.None);
            var restored = await File.ReadAllBytesAsync(fixture.ConfigurationPath);
            Assert.Equal(backup[fixture.ConfigurationPath], restored);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RecreatesSignedSourceSubtreeRemovedByApplicationCache()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var discovered = Assert.Single(await store.CaptureDocumentsAsync(CancellationToken.None));
            var sourceValues = discovered.Values
                .Where(value => value.JsonPointer.StartsWith(CameraSourcePointer, StringComparison.Ordinal))
                .ToArray();
            var document = discovered with { Values = sourceValues };

            var root = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath))!.AsObject();
            root["sourceStore"]!["sceneSource"]!["scene4"]!["data"] = new JsonObject();
            await File.WriteAllTextAsync(fixture.ConfigurationPath, root.ToJsonString());

            await store.ApplyAsync(
                [document],
                new Dictionary<Guid, DeviceMapping>(),
                [],
                fixture.RootPath,
                CancellationToken.None);

            using var applied = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath));
            var restoredSource = applied.RootElement.GetProperty("sourceStore")
                .GetProperty("sceneSource")
                .GetProperty("scene4")
                .GetProperty("data")
                .GetProperty("source-1");
            Assert.Equal(
                "capture-card-a",
                restoredSource.GetProperty("payload").GetProperty("deviceId").GetString());
            Assert.Equal(
                "人像调色",
                restoredSource.GetProperty("payload").GetProperty("filterChain")[0]
                    .GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RepairTargetAllowsMissingChildrenButRejectsMissingSignedRoot()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    @"WBStore\sourceStore.json",
                    null,
                    true)],
                [new FieldMappingDefinition(
                    "device",
                    UnifiedFieldKind.DeviceSelection,
                    "main",
                    $"{CameraSourcePointer}/payload/deviceId",
                    "string",
                    true,
                    true)],
                [],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var adapter = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
            var root = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath))!.AsObject();
            root["sourceStore"]!["sceneSource"]!["scene4"]!["data"] = new JsonObject();
            await File.WriteAllTextAsync(fixture.ConfigurationPath, root.ToJsonString());

            await store.ValidateRepairTargetAsync(adapter, CancellationToken.None);

            root.Remove("sourceStore");
            await File.WriteAllTextAsync(fixture.ConfigurationPath, root.ToJsonString());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ValidateRepairTargetAsync(adapter, CancellationToken.None));
            Assert.Contains("sourceStore", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RepairTargetRejectsReadOnlyConfigurationBeforeRestoreTransaction()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    @"WBStore\sourceStore.json",
                    null,
                    true)],
                [new FieldMappingDefinition(
                    "device",
                    UnifiedFieldKind.DeviceSelection,
                    "main",
                    $"{CameraSourcePointer}/payload/deviceId",
                    "string",
                    true,
                    true)],
                [],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var adapter = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
            File.SetAttributes(
                fixture.ConfigurationPath,
                File.GetAttributes(fixture.ConfigurationPath) | FileAttributes.ReadOnly);

            var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                store.ValidateRepairTargetAsync(adapter, CancellationToken.None));

            Assert.Contains("只读", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(fixture.ConfigurationPath, FileAttributes.Normal);
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task DetectsExplicitIdleAndLiveStates()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var idle = await store.InspectLiveStateAsync(CancellationToken.None);
            Assert.True(idle.CanDetermine);
            Assert.False(idle.IsLive);

            var liveContent = OriginalConfiguration(fixture.LutPath).Replace(
                "\"isLive\": false",
                "\"isLive\": true",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixture.ConfigurationPath, liveContent);
            var live = await store.InspectLiveStateAsync(CancellationToken.None);
            Assert.True(live.CanDetermine);
            Assert.True(live.IsLive);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SignedDefinitionCapturesOnlyDeclaredFieldsWithExactTypes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    @"WBStore\sourceStore.json",
                    null,
                    true)],
                [
                    new FieldMappingDefinition(
                        "device",
                        UnifiedFieldKind.DeviceSelection,
                        "main",
                        $"{CameraSourcePointer}/payload/deviceId",
                        "string",
                        true,
                        true),
                    new FieldMappingDefinition(
                        "width",
                        UnifiedFieldKind.Width,
                        "main",
                        $"{CameraSourcePointer}/payload/width",
                        "int",
                        true,
                        false,
                        ControlKind: "ApplicationManaged")
                ],
                ["/login"],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var verified = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));

            var documents = await store.CaptureDefinedDocumentsAsync(verified, CancellationToken.None);
            var liveState = await store.InspectDefinedLiveStateAsync(verified, CancellationToken.None);

            var document = Assert.Single(documents);
            LiveCompanionConfigurationStore.ValidateDefinedDocuments(verified, documents);
            var writableDocument = Assert.Single(
                LiveCompanionConfigurationStore.SelectWritableDocuments(verified, documents));
            Assert.Equal("fixture-adapter", document.StructureVersion);
            Assert.Equal(2, document.Values.Count);
            Assert.Single(writableDocument.Values);
            Assert.DoesNotContain(
                writableDocument.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/width");
            Assert.Contains(document.Values, value => value.JsonPointer == $"{CameraSourcePointer}/payload/deviceId");
            Assert.Contains(document.Values, value => value.JsonPointer == $"{CameraSourcePointer}/payload/width");
            Assert.DoesNotContain(document.Values, value => value.JsonPointer.Contains("login", StringComparison.OrdinalIgnoreCase));
            Assert.True(liveState.CanDetermine);
            Assert.False(liveState.IsLive);

            var injected = document with
            {
                Values = document.Values.Append(new NativeConfigurationValue(
                    "/login/token",
                    "Filter",
                    JsonSerializer.SerializeToElement("injected"))).ToArray()
            };
            Assert.Throws<InvalidOperationException>(() =>
                LiveCompanionConfigurationStore.ValidateDefinedDocuments(verified, [injected]));
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SignedDefinitionSkipsOptionalApplicationManagedFieldWhenRuntimeTypeDrifts()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    @"WBStore\sourceStore.json",
                    null,
                    true)],
                [
                    new FieldMappingDefinition(
                        "device",
                        UnifiedFieldKind.DeviceSelection,
                        "main",
                        $"{CameraSourcePointer}/payload/deviceId",
                        "string",
                        true,
                        true),
                    new FieldMappingDefinition(
                        "runtime-cache",
                        UnifiedFieldKind.NativeField,
                        "main",
                        $"{CameraSourcePointer}/payload/width",
                        "bool",
                        false,
                        false,
                        ControlKind: "ApplicationManaged")
                ],
                ["/login"],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var verified = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));

            var documents = await store.CaptureDefinedDocumentsAsync(verified, CancellationToken.None);

            var document = Assert.Single(documents);
            LiveCompanionConfigurationStore.ValidateDefinedDocuments(verified, documents);
            Assert.Single(document.Values);
            Assert.Contains(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/deviceId");
            Assert.DoesNotContain(
                document.Values,
                value => value.JsonPointer == $"{CameraSourcePointer}/payload/width");
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SignedDefinitionRejectsRuntimeTypeDriftForRestorableField()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    @"WBStore\sourceStore.json",
                    null,
                    true)],
                [new FieldMappingDefinition(
                    "device",
                    UnifiedFieldKind.DeviceSelection,
                    "main",
                    $"{CameraSourcePointer}/payload/deviceId",
                    "number",
                    true,
                    true)],
                ["/login"],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var verified = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CaptureDefinedDocumentsAsync(verified, CancellationToken.None));

            Assert.Contains("字段类型不匹配", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SignedDefinitionBindsApplicationManagedSourceAndEffectIdentifiers()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-live-companion-binding-{Guid.NewGuid():N}");
        var storeDirectory = Path.Combine(root, "WBStore");
        Directory.CreateDirectory(storeDirectory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(storeDirectory, "sourceStore.json"),
                """
                {
                  "sourceStore": {
                    "sceneSource": {
                      "runtime-scene": {
                        "data": {
                          "runtime-source": {
                            "type": "camera",
                            "effectConfigId": "runtime-effect",
                            "payload": { "deviceId": "OBS Virtual Camera" }
                          }
                        }
                      }
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(storeDirectory, "effectConfigStore.json"),
                """
                {
                  "effectConfigStore": {
                    "configs": {
                      "runtime-effect": { "strength": 0.4 }
                    }
                  }
                }
                """);
            var definition = new LiveCompanionAdapterDefinition(
                "runtime-binding-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [
                    new ConfigurationStoreDefinition(
                        "source-store",
                        ConfigurationStorageKind.JsonFile,
                        @"WBStore\sourceStore.json",
                        null,
                        true),
                    new ConfigurationStoreDefinition(
                        "effect-config",
                        ConfigurationStorageKind.JsonFile,
                        @"WBStore\effectConfigStore.json",
                        null,
                        true)
                ],
                [
                    new FieldMappingDefinition(
                        "device",
                        UnifiedFieldKind.DeviceSelection,
                        "source-store",
                        "/sourceStore/sceneSource/canonical-scene/data/canonical-source/payload/deviceId",
                        "string",
                        true,
                        true),
                    new FieldMappingDefinition(
                        "effect-id",
                        UnifiedFieldKind.NativeField,
                        "source-store",
                        "/sourceStore/sceneSource/canonical-scene/data/canonical-source/effectConfigId",
                        "string",
                        true,
                        true),
                    new FieldMappingDefinition(
                        "strength",
                        UnifiedFieldKind.FilterSetting,
                        "effect-config",
                        "/effectConfigStore/configs/canonical-effect/strength",
                        "number",
                        true,
                        true)
                ],
                [],
                new LiveStateRuleDefinition("source-store", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var verified = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
            var store = new LiveCompanionConfigurationStore(root);

            var discovered = await store.CaptureDocumentsAsync(CancellationToken.None);
            Assert.True(LiveCompanionAdapterCatalog.MatchesRequiredShape(definition, discovered));

            var captured = await store.CaptureDefinedDocumentsAsync(verified, CancellationToken.None);
            var values = captured.SelectMany(document => document.Values).ToDictionary(
                value => value.JsonPointer,
                StringComparer.Ordinal);
            Assert.Equal(
                "OBS Virtual Camera",
                values["/sourceStore/sceneSource/canonical-scene/data/canonical-source/payload/deviceId"].Value.GetString());
            Assert.Equal(
                "canonical-effect",
                values["/sourceStore/sceneSource/canonical-scene/data/canonical-source/effectConfigId"].Value.GetString());
            Assert.Equal(
                0.4,
                values["/effectConfigStore/configs/canonical-effect/strength"].Value.GetDouble());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StableStoreFilesCompleteAfterMinimumObservationInsteadOfFullTimeout()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var timer = Stopwatch.StartNew();

            await store.WaitForStableStoreFilesAsync(
                CreateSingleStoreAdapter(),
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20),
                2,
                CancellationToken.None);

            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(1));
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task StableStoreFilesRetryTransientInvalidJsonAndStillRequireMatchingCaptures()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.ConfigurationPath, "{", Encoding.UTF8);
            var repair = Task.Run(async () =>
            {
                await Task.Delay(80);
                await File.WriteAllTextAsync(
                    fixture.ConfigurationPath,
                    OriginalConfiguration(fixture.LutPath),
                    Encoding.UTF8);
            });
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var timer = Stopwatch.StartNew();

            await store.WaitForStableStoreFilesAsync(
                CreateSingleStoreAdapter(),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20),
                2,
                CancellationToken.None);
            await repair;

            Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(80));
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task StableStoreFilesRejectPermanentlyInvalidJsonAtOriginalSafetyDeadline()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.ConfigurationPath, "{", Encoding.UTF8);
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);

            var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
                store.WaitForStableStoreFilesAsync(
                    CreateSingleStoreAdapter(),
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromMilliseconds(140),
                    TimeSpan.FromMilliseconds(20),
                    2,
                    CancellationToken.None));

            Assert.Contains("未稳定", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    private static VerifiedAdapterDefinition CreateSingleStoreAdapter()
    {
        var definition = new LiveCompanionAdapterDefinition(
            "stable-store-test",
            "1.0.0",
            "1.0.0",
            new string('a', 64),
            [new ConfigurationStoreDefinition(
                "main",
                ConfigurationStorageKind.JsonFile,
                @"WBStore\sourceStore.json",
                null,
                true)],
            [],
            [],
            new LiveStateRuleDefinition("main", "/isLive", "false"),
            new ScreenshotRuleDefinition("window", "main"));
        return new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
    }

    private static async Task<LiveCompanionFixture> CreateFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-live-companion-{Guid.NewGuid():N}");
        var storeDirectory = Path.Combine(root, "WBStore");
        Directory.CreateDirectory(storeDirectory);
        var lutPath = Path.Combine(root, "portrait.cube");
        var configurationPath = Path.Combine(storeDirectory, "sourceStore.json");
        await File.WriteAllTextAsync(lutPath, "LUT_3D_SIZE 2", Encoding.UTF8);
        await File.WriteAllTextAsync(configurationPath, OriginalConfiguration(lutPath), Encoding.UTF8);
        return new LiveCompanionFixture(root, configurationPath, lutPath);
    }

    private static string OriginalConfiguration(string lutPath) => $$"""
        {
          "isLive": false,
          "login": { "token": "secret-token" },
          "unrelated": "keep-me",
          "sourceStore": {
            "sceneSource": {
              "scene4": {
                "data": {
                  "source-1": {
                    "name": "摄像头",
                    "type": "camera",
                    "payload": {
                      "deviceId": "capture-card-a",
                      "width": 1920,
                      "height": 1080,
                      "rate": 60,
                      "format": "NV12",
                      "colorSpace": "Rec.709",
                      "videoRange": "Partial",
                      "speechConfig": "{\"appToken\":\"embedded-token\",\"address\":\"wss://example.invalid\"}",
                      "filterChain": [
                        {
                          "name": "人像调色",
                          "type": "color_filter",
                          "enabled": true,
                          "order": 0,
                          "contrast": 0.12,
                          "imagePath": {{JsonSerializer.Serialize(lutPath)}}
                        }
                      ]
                    },
                    "effectConfigId": "effect-1"
                  }
                }
              }
            }
          }
        }
        """;

    private static string ChangedConfiguration(string lutPath) => OriginalConfiguration(lutPath)
        .Replace("capture-card-a", "changed-device", StringComparison.Ordinal)
        .Replace("1920", "1280", StringComparison.Ordinal)
        .Replace("0.12", "0.01", StringComparison.Ordinal);

    private sealed record LiveCompanionFixture(
        string RootPath,
        string ConfigurationPath,
        string LutPath);
}
