using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionCrossMachineTests
{
    [Theory]
    [InlineData("data", "data2")]
    [InlineData("data2", "data")]
    [InlineData("data2", "data2")]
    public async Task RestoresCameraAcrossContainersAndVerifiesEveryInstance(string from, string to)
    {
        using var fixture = new Fixture();
        var expectedSource = Source(from, "original", 1920);
        var expectedEffect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/curve/0", 0)]);
        var profile = LiveCompanionPortableProfile.TryCreate([expectedSource, expectedEffect]);
        Assert.NotNull(profile);
        Assert.Equal(from, profile.Camera.Container);
        // Identical IDs on two canvases must remain two targets, never merge by dictionary key.
        var root = LiveCompanionCameraPayloadStore.ReconstructSourceStore(Source(to, "current", 1280));
        var other = to == "data" ? "data2" : "data";
        var scene = root["sourceStore"]!["sceneSource"]!["scene"]!;
        scene[other] = scene[to]!.DeepClone();
        foreach (var container in new[] { to, other })
        {
            scene[container]!["current"]!["secondSource"] = container == "data2";
            scene[container]!["current"]!["viewIndex"] = container == "data2" ? 1 : 0;
            scene[container]!["current"]!["name"] = "target-source-name";
            scene[container]!["text"] = new JsonObject { ["type"] = "text", ["payload"] = new JsonObject { ["text"] = "keep" } };
        }
        await File.WriteAllTextAsync(fixture.SourcePath, root.ToJsonString());
        await File.WriteAllTextAsync(fixture.EffectPath, """{"effectConfigStore":{"configs":{"effect":{"curve":[1,2,3]}}}}""");
        var cameraStore = new LiveCompanionCameraPayloadStore(fixture.Root);
        var active = await cameraStore.GetActiveCamerasAsync(CancellationToken.None);
        Assert.Equal(2, active.Count);
        var expected = new[] { profile.SourceStoreDocument, profile.EffectConfigurationDocument };
        Assert.NotEmpty(await LiveCompanionRestoreVerifier.VerifyAllAsync(fixture.Root, expected, profile.Camera, active, CancellationToken.None));
        var backup = await fixture.Store.BackupAsync(expected, CancellationToken.None);
        Assert.Equal(2, await cameraStore.ApplyPortableToActiveSourcesAsync(profile.SourceStoreDocument, CancellationToken.None));
        await fixture.Store.ApplyPortableBoundDocumentsAsync(expected, profile.Camera, active, CancellationToken.None);
        Assert.Empty(await LiveCompanionRestoreVerifier.VerifyAllAsync(fixture.Root, expected, profile.Camera, active, CancellationToken.None));
        var actual = JsonNode.Parse(await File.ReadAllTextAsync(fixture.SourcePath))!["sourceStore"]!["sceneSource"]!["scene"]!;
        Assert.True(actual["data2"]!["current"]!["secondSource"]!.GetValue<bool>());
        Assert.False(actual["data"]!["current"]!["secondSource"]!.GetValue<bool>());
        Assert.Equal("target-source-name", actual[to]!["current"]!["name"]!.GetValue<string>());
        Assert.Equal("keep", actual[to]!["text"]!["payload"]!["text"]!.GetValue<string>());
        // Rollback must preserve the complete original documents including excluded scene content.
        await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, CancellationToken.None);
        foreach (var entry in backup) { Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key)); }
    }

    [Fact]
    public void SecondaryCanvasMatchesSignedPathsWithoutReplacingDataValues()
    {
        var source = Source("data2", "runtime", 1920);
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", "data")]);
        var definition = new LiveCompanionAdapterDefinition("fixture", "1.0", "1.0", new string('a', 64),
            [new("source-store", ConfigurationStorageKind.JsonFile, "WBStore/sourceStore.json", null, true),
             new("effect-config", ConfigurationStorageKind.JsonFile, "WBStore/effectConfigStore.json", null, true)],
            [new("width", UnifiedFieldKind.Width, "source-store", "/sourceStore/sceneSource/canonical/data/camera/payload/width", "number", true, true),
             new("value", UnifiedFieldKind.NativeField, "effect-config", "/effectConfigStore/configs/canonical-effect/value", "string", true, true)],
            [], new("source-store", "/idle", "false"), new("window", "main"));
        var binding = LiveCompanionRuntimeBinding.TryCreate(definition, [source, effect]);
        Assert.NotNull(binding);
        Assert.Equal("/sourceStore/sceneSource/scene/data2/runtime/payload/width", binding.ToRuntimePointer(definition.Fields[0].NativePath));
        Assert.Equal("/effectConfigStore/configs/effect/data", binding.ToRuntimePointer("/effectConfigStore/configs/canonical-effect/data"));
        Assert.Equal("data", binding.ToCanonicalValue(JsonSerializer.SerializeToElement("data")).GetString());
        Assert.True(LiveCompanionAdapterCatalog.MatchesRequiredShape(definition, [source, effect]));
    }

    [Fact]
    public void EquivalentCamerasOnBothCanvasesAreCapturedAndContextIsNotRestored()
    {
        var first = Source("data", "first", 1920);
        var second = Source("data2", "second", 1920);
        var combined = first with { Values = first.Values.Concat(second.Values).ToArray() };
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", 1)]);
        var profile = LiveCompanionPortableProfile.TryCreate([combined, effect], out var reason);
        Assert.NotNull(profile);
        Assert.Empty(reason);
        Assert.DoesNotContain(profile.SourceStoreDocument.Values, field => field.JsonPointer.EndsWith("/viewIndex", StringComparison.Ordinal));
        Assert.Equal(2, LiveCompanionCameraPayloadStore.GetTargets(combined).Count);
    }

    [Theory]
    [InlineData("missing", "不存在")]
    [InlineData("empty", "文件为空")]
    [InlineData("invalid", "JSON 无效")]
    [InlineData("array", "根节点不是对象")]
    [InlineData("unknown", "没有可读取的目标字段")]
    [InlineData("locked", "文件被独占")]
    public async Task ReadFailuresHaveSpecificSafeReasons(string state, string reason)
    {
        using var fixture = new Fixture();
        if (state != "missing")
        {
            await File.WriteAllTextAsync(fixture.SourcePath, state switch
            {
                "empty" => "",
                "invalid" => "{private-secret-value",
                "array" => "[]",
                _ => "{}"
            });
        }
        using var locked = state == "locked" ? new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var error = await Assert.ThrowsAsync<LiveCompanionConfigurationReadException>(() => fixture.Store.CaptureDocumentsAsync(CancellationToken.None));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableDocumentCannotProducePartialCapture()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, LiveCompanionCameraPayloadStore.ReconstructSourceStore(Source("data", "first", 1920)).ToJsonString());
        await File.WriteAllTextAsync(fixture.EffectPath, "{");
        await Assert.ThrowsAsync<LiveCompanionConfigurationReadException>(() => fixture.Store.CaptureDocumentsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("roaming")]
    [InlineData("local")]
    [InlineData("nested")]
    public void FindsUniqueApplicationRoot(string location)
    {
        using var fixture = new Fixture();
        var roaming = Path.Combine(fixture.Root, "roaming");
        var local = Path.Combine(fixture.Root, "local");
        var expected = location switch { "local" => local, "nested" => Path.Combine(roaming, "profile"), _ => roaming };
        Directory.CreateDirectory(Path.Combine(expected, "WBStore"));
        Assert.Equal(expected, LiveCompanionConfigurationLocation.Resolve(roaming, local));
    }

    [Fact]
    public void MultipleApplicationRootsCannotBeChosenByModificationTime()
    {
        using var fixture = new Fixture();
        var roaming = Path.Combine(fixture.Root, "roaming");
        var local = Path.Combine(fixture.Root, "local");
        Directory.CreateDirectory(Path.Combine(roaming, "WBStore"));
        Directory.CreateDirectory(Path.Combine(local, "WBStore"));
        Assert.Throws<LiveCompanionConfigurationReadException>(() => LiveCompanionConfigurationLocation.Resolve(roaming, local));
    }

    [Fact]
    public async Task IncompleteSecondaryCameraCannotBeHiddenByValidPrimaryCamera()
    {
        using var fixture = new Fixture();
        var root = LiveCompanionCameraPayloadStore.ReconstructSourceStore(Source("data", "first", 1920));
        root["sourceStore"]!["sceneSource"]!["scene"]!["data2"] =
            new JsonObject { ["broken"] = new JsonObject { ["type"] = "camera" } };
        await File.WriteAllTextAsync(fixture.SourcePath, root.ToJsonString());
        var documents = await fixture.Store.CaptureDocumentsAsync(CancellationToken.None);
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", 1)]);
        Assert.Null(LiveCompanionPortableProfile.TryCreate(documents.Concat([effect]).ToArray(), out var reason));
        Assert.Contains("设备载荷", reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LiveCompanionCameraPayloadStore(fixture.Root).GetActiveCamerasAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AtomicReplacementWaitsForShortLivedReaderWithoutDeletingDestination()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "{}");
        using var reader = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(fixture.SourcePath, new JsonObject { ["restored"] = true }, CancellationToken.None);
        await Task.Delay(150);
        Assert.True(File.Exists(fixture.SourcePath));
        reader.Dispose();
        await write;
        Assert.True(JsonNode.Parse(await File.ReadAllTextAsync(fixture.SourcePath))!["restored"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PersistentReplacementDenialPreservesOriginalAndRemovesTemporaryFile()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "{}");
        using var reader = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = await Record.ExceptionAsync(() => LiveCompanionConfigurationStore.WriteJsonAtomicallyAsync(
            fixture.SourcePath, new JsonObject { ["restored"] = true }, CancellationToken.None));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("{}", await File.ReadAllTextAsync(fixture.SourcePath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void PropertyOrderDoesNotMakeEquivalentCameraPayloadsDifferent()
    {
        var first = Source("data", "first", 1920);
        var second = Source("data2", "second", 1920);
        var combined = first with { Values = first.Values.Concat(second.Values.Reverse()).ToArray() };
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", 1)]);
        Assert.NotNull(LiveCompanionPortableProfile.TryCreate([combined, effect]));
    }

    private static NativeConfigurationDocument Source(string container, string id, int width)
    {
        var prefix = $"/sourceStore/sceneSource/scene/{container}/{id}";
        return Document("sourceStore", [Value(prefix + "/type", "camera"), Value(prefix + "/effectConfigId", "effect"),
            Value(prefix + "/viewIndex", container == "data2" ? 1 : 0), Value(prefix + "/secondSource", container == "data2"),
            Value(prefix + "/name", "original-source-name"), Value(prefix + "/payload/deviceId", "device"),
            Value(prefix + "/payload/width", width), Value(prefix + "/payload/height", 1080),
            Value(prefix + "/payload/format", 3), Value(prefix + "/payload/rate", 30)]);
    }
    private static NativeConfigurationValue Value<T>(string path, T value) => new(path, NativeParameterCategories.Filter, JsonSerializer.SerializeToElement(value));
    private static NativeConfigurationDocument Document(string name, IReadOnlyList<NativeConfigurationValue> values) =>
        new("webcast_mate", "JsonFile", "json-v1", $"WBStore/{name}.json", $"WBStore/{name}.json", new string('a', 64), Guid.NewGuid(), values);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LiveStudio-cross-machine-" + Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "WBStore", "sourceStore.json");
        public string EffectPath => Path.Combine(Root, "WBStore", "effectConfigStore.json");
        public LiveCompanionConfigurationStore Store => new(Root);
        public Fixture() => Directory.CreateDirectory(Path.Combine(Root, "WBStore"));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
