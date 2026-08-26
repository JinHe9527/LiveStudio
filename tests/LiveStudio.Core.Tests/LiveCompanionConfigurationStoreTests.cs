using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionConfigurationStoreTests
{
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
