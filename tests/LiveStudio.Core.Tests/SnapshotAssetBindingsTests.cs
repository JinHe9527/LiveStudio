using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotAssetBindingsTests
{
    [Fact]
    public void CollectsAssetsDeclaredOnlyBySchemaV3FilterChains()
    {
        var binding = new AssetBinding(
            Guid.NewGuid(),
            new string('a', 64),
            "portrait.cube",
            @"C:\assets\portrait.cube",
            "/filters/0/settings/path",
            123);
        var application = CreateApplication([
            new FilterChainSnapshot(
                Guid.NewGuid(),
                "滤镜效果",
                "滤镜设置/滤镜效果",
                true,
                [new FilterInstanceSnapshot(
                    Guid.NewGuid(),
                    "LUT",
                    "lut",
                    "lut-1",
                    true,
                    0,
                    new Dictionary<string, JsonElement>(),
                    [binding],
                    FieldEvidenceStatus.Mapped)])
        ]);

        Assert.Equal(binding, Assert.Single(SnapshotAssetBindings.Collect([application])));
    }

    [Fact]
    public void RejectsConflictingDefinitionsForTheSameBindingId()
    {
        var bindingId = Guid.NewGuid();
        var sourceBinding = new AssetBinding(
            bindingId,
            new string('a', 64),
            "portrait.cube",
            @"C:\assets\portrait.cube",
            "/filters/0/settings/path",
            123);
        var chainBinding = sourceBinding with { Length = 124 };
        var source = new VideoSource(
            Guid.NewGuid(),
            "摄像头",
            "dshow_input",
            null,
            null,
            new Dictionary<string, JsonElement>(),
            [new VideoFilter(
                Guid.NewGuid(),
                "LUT",
                "clut_filter",
                true,
                0,
                new Dictionary<string, JsonElement>(),
                [sourceBinding])]);
        var application = CreateApplication([
            new FilterChainSnapshot(
                source.LogicalId,
                "视频滤镜",
                "OBS/摄像头/视频滤镜",
                null,
                [new FilterInstanceSnapshot(
                    Guid.NewGuid(),
                    "LUT",
                    "clut_filter",
                    null,
                    true,
                    0,
                    new Dictionary<string, JsonElement>(),
                    [chainBinding],
                    FieldEvidenceStatus.Mapped)])
        ]) with
        { Sources = [source] };

        Assert.Throws<InvalidDataException>(() => SnapshotAssetBindings.Collect([application]));
    }

    private static ApplicationSnapshot CreateApplication(IReadOnlyList<FilterChainSnapshot> filterChains) => new(
        ApplicationKind.Obs,
        "31.0.0",
        "obs-websocket-5",
        string.Empty,
        new string('b', 64),
        CompatibilityLevel.Experimental,
        true,
        [],
        [],
        [],
        null,
        filterChains,
        new CaptureConsistency("DoubleRead", new string('c', 64), new string('c', 64), 1, true));
}
