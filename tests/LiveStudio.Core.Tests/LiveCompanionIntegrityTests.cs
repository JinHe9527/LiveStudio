using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionIntegrityTests
{
    [Fact]
    public void ExactVersionDefinitionWinsOverNewerRevisionForOtherVersions()
    {
        var first = new LiveCompanionAdapterDefinition("fixture-v3", "12.8.1.0", "12.9.2.0", new string('a', 64),
            [], [], [], new LiveStateRuleDefinition("main", "/idle", "false"), new ScreenshotRuleDefinition("window", "main"));
        var second = first with { Id = "fixture-v4", MinimumVersion = "12.9.3.0", MaximumVersion = "12.9.3.0" };
        VerifiedAdapterDefinition[] definitions = [new(first, "test", new string('b', 64)), new(second, "test", new string('c', 64))];
        Assert.Equal(first.Id, CompatibilityMatcher.MatchPortableCapabilityCandidates("12.9.2.0", definitions).Adapter?.Definition.Id);
        Assert.Equal(second.Id, CompatibilityMatcher.MatchPortableCapabilityCandidates("12.9.3.0", definitions).Adapter?.Definition.Id);
    }

    [Fact]
    public async Task OneMatchingCameraCannotHideAnotherMismatchingInstance()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.EffectPath,
            """{"effectConfigStore":{"configs":{"a":{"value":1},"b":{"value":2}}}}""");
        var expected = Document("effectConfigStore", [Value("/effectConfigStore/configs/original/value", 1)]);
        var camera = new LiveCompanionCameraTarget("original-scene", "original-source", "device", "original", new JsonObject());
        LiveCompanionActiveCamera[] active = [new("scene", "first", "device", "a"), new("scene", "second", "device", "b")];
        var differences = await LiveCompanionRestoreVerifier.VerifyAllAsync(fixture.Root, [expected], camera, active, CancellationToken.None);
        Assert.Contains("scene/second", Assert.Single(differences), StringComparison.Ordinal);
        Assert.NotEmpty(await LiveCompanionRestoreVerifier.VerifyAllAsync(fixture.Root, [expected], camera, [], CancellationToken.None));
    }

    [Fact]
    public async Task DiscoveryRetainsNullIncludingArrayPositionsAndExcludesCredentials()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.EffectPath,
            """{"effectConfigStore":{"configs":{"effect":{"value":null,"curve":[null,0,null],"accessToken":"private"}}}}""");
        var document = Assert.Single(await fixture.Store.CaptureDocumentsAsync(CancellationToken.None));
        Assert.Equal(3, document.Values.Count(value => value.Value.ValueKind == JsonValueKind.Null));
        Assert.Contains(document.Values, value => value.JsonPointer.EndsWith("/curve/2", StringComparison.Ordinal));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"x\":0},{\"x\":1}]")]
    public void PortableMergeRemovesTrailingCurvePoints(string expectedJson)
    {
        var current = JsonNode.Parse("""[{"x":0},{"x":1},{"x":2}]""")!;
        var expected = JsonNode.Parse(expectedJson)!;
        Assert.True(JsonNode.DeepEquals(expected,
            LiveCompanionCameraPayloadStore.MergePortablePayload(current, expected)));
        Assert.Equal(3, current.AsArray().Count);
    }

    [Fact]
    public void SecondaryCameraCannotBeSilentlyOmittedFromCaptureOrPreflight()
    {
        var source = Document("sourceStore", CameraValues("data", "first", "device")
            .Concat(CameraValues("data2", "second", "other-device")));
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", 1)]);
        Assert.Null(LiveCompanionPortableProfile.TryCreate([source, effect], out var reason));
        Assert.Contains("data2", reason, StringComparison.Ordinal);
        Assert.False(LiveCompanionPortableProfile.ValidateTargetSelection([source, effect]).CanProceed);
    }

    [Fact]
    public void DifferentCameraTargetsFailBeforeWriting()
    {
        var source = Document("sourceStore", CameraValues("data", "first", "device")
            .Concat(CameraValues("data", "second", "other-device")));
        var effect = Document("effectConfigStore", [Value("/effectConfigStore/configs/effect/value", 1)]);
        Assert.False(LiveCompanionPortableProfile.ValidateTargetSelection([source, effect]).CanProceed);
    }

    [Fact]
    public void MissingCameraRemainsEligibleForNativeReconstruction()
    {
        var source = Document("sourceStore", [Value("/sourceStore/sceneSource/scene/data", new { })]);
        Assert.True(LiveCompanionPortableProfile.ValidateTargetSelection([source]).CanProceed);
    }

    [Fact]
    public void DuplicateDevicePayloadCannotOverwriteAnotherConfiguration()
    {
        var source = Document("sourceStore", CameraValues("data", "first", "device")
            .Concat(CameraValues("data", "second", "device").Select(value =>
                value.JsonPointer.EndsWith("/width", StringComparison.Ordinal) ? Value(value.JsonPointer, 1280) : value)));
        Assert.Throws<InvalidOperationException>(() => LiveCompanionCameraPayloadStore.FindCameraPayloads(
            LiveCompanionCameraPayloadStore.ReconstructSourceStore(source)));
    }

    [Fact]
    public async Task ExtraNestedCurvePointsFailReadbackAndAreRemovedOnApply()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.EffectPath,
            """{"effectConfigStore":{"configs":{"effect":{"curves":[{"points":[0,1,2]},{"points":[3]}],"keep":"target"}}}}""");
        var expected = Document("effectConfigStore",
            [Value("/effectConfigStore/configs/effect/curves/0/points/0", 0)]);
        var camera = new LiveCompanionCameraTarget("scene", "camera", "device", "effect", new JsonObject());
        var active = new LiveCompanionActiveCamera("scene", "camera", "device", "effect");
        var before = await LiveCompanionRestoreVerifier.VerifyAsync(fixture.Root, [expected], camera, active, CancellationToken.None);
        Assert.Equal(2, before.Count);
        Assert.All(before, difference => Assert.Contains("数组长度不一致", difference, StringComparison.Ordinal));
        await fixture.Store.ApplyPortableBoundDocumentsAsync([expected], camera, [active], CancellationToken.None);
        Assert.Empty(await LiveCompanionRestoreVerifier.VerifyAsync(fixture.Root, [expected], camera, active, CancellationToken.None));
        var result = JsonNode.Parse(await File.ReadAllTextAsync(fixture.EffectPath))!;
        Assert.Equal("target", result["effectConfigStore"]!["configs"]!["effect"]!["keep"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("readonly")]
    [InlineData("valid")]
    public async Task AuthoritativeCameraFileIsCheckedBeforeTransaction(string state)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "storage", "camera-payloads.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (state != "missing") { await File.WriteAllTextAsync(path, state == "invalid" ? "[]" : "{}"); }
        if (state == "readonly") { File.SetAttributes(path, FileAttributes.ReadOnly); }
        var definition = new LiveCompanionAdapterDefinition("fixture", "1.0", "1.0", new string('a', 64),
            [], [], [], new LiveStateRuleDefinition("main", "/idle", "false"), new ScreenshotRuleDefinition("window", "main"));
        var adapter = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));
        try
        {
            if (state == "valid") { await fixture.Store.ValidateRepairTargetAsync(adapter, CancellationToken.None); }
            else if (state == "readonly")
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ValidateRepairTargetAsync(adapter, CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ValidateRepairTargetAsync(adapter, CancellationToken.None));
            }
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); } }
    }

    private static IEnumerable<NativeConfigurationValue> CameraValues(string container, string id, string device)
    {
        var prefix = $"/sourceStore/sceneSource/scene/{container}/{id}";
        return [Value(prefix + "/type", "camera"), Value(prefix + "/effectConfigId", "effect"),
            Value(prefix + "/payload/deviceId", device), Value(prefix + "/payload/width", 1920),
            Value(prefix + "/payload/height", 1080), Value(prefix + "/payload/format", 3), Value(prefix + "/payload/rate", 30)];
    }

    private static NativeConfigurationValue Value<T>(string pointer, T value) =>
        new(pointer, NativeParameterCategories.Filter, JsonSerializer.SerializeToElement(value));

    private static NativeConfigurationDocument Document(string name, IEnumerable<NativeConfigurationValue> values) =>
        new("webcast_mate", "JsonFile", "json-v1", $"WBStore/{name}.json", $"WBStore/{name}.json", new string('a', 64), Guid.NewGuid(), values.ToArray());

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LiveStudio-integrity-" + Guid.NewGuid().ToString("N"));
        public string EffectPath => Path.Combine(Root, "WBStore", "effectConfigStore.json");
        public LiveCompanionConfigurationStore Store => new(Root);
        public Fixture() => Directory.CreateDirectory(Path.Combine(Root, "WBStore"));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
