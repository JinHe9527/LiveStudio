using System.Text.Json;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class ObsSourceSettingsTests
{
    [Fact]
    public void TargetSettingsPreserveUnknownNestedVideoParameters()
    {
        var sourceId = Guid.NewGuid();
        var nestedParameter = JsonSerializer.SerializeToElement(new
        {
            curve = new[]
            {
                new { x = 0.0, y = 0.0 },
                new { x = 0.45, y = 0.61 }
            },
            interpolation = "smooth"
        });
        var source = new VideoSource(
            sourceId,
            "采集卡",
            "dshow_input",
            null,
            null,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["video_device_id"] = JsonSerializer.SerializeToElement("source-device"),
                ["vendor_private_video_parameters"] = nestedParameter
            },
            []);
        var mapping = new DeviceMapping(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            sourceId,
            ApplicationKind.Obs,
            "target-device",
            "采集卡",
            "直播场景",
            false);

        var target = ObsSnapshotMapper.CreateTargetSettings(source, mapping);

        Assert.Equal("target-device", target["video_device_id"].GetString());
        Assert.True(JsonElement.DeepEquals(nestedParameter, target["vendor_private_video_parameters"]));
    }

    [Fact]
    public void MonitorCaptureDoesNotReceiveCameraDeviceField()
    {
        var source = new VideoSource(
            Guid.NewGuid(),
            "显示器采集",
            "monitor_capture",
            null,
            null,
            new Dictionary<string, JsonElement>
            {
                ["monitor_id"] = JsonSerializer.SerializeToElement("display-1")
            },
            []);
        var mapping = new DeviceMapping(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            source.LogicalId,
            ApplicationKind.Obs,
            string.Empty,
            source.Name,
            string.Empty,
            false);

        var target = ObsSnapshotMapper.CreateTargetSettings(source, mapping);

        Assert.Equal("display-1", target["monitor_id"].GetString());
        Assert.DoesNotContain("video_device_id", target.Keys);
    }

    [Fact]
    public void EffectiveFilterSettingsIncludeDefaultsAndPreferExplicitValues()
    {
        var defaults = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sharpness"] = JsonSerializer.SerializeToElement(0.08),
            ["vendor_default"] = JsonSerializer.SerializeToElement(true)
        };
        var explicitSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sharpness"] = JsonSerializer.SerializeToElement(0.25)
        };

        var effective = ObsSnapshotMapper.MergeEffectiveFilterSettings(defaults, explicitSettings);

        Assert.Equal(0.25, effective["sharpness"].GetDouble());
        Assert.True(effective["vendor_default"].GetBoolean());
        Assert.Equal(0.08, defaults["sharpness"].GetDouble());
    }

    [Fact]
    public void EffectiveMappingRecreatesMissingOriginalSourceInCurrentScene()
    {
        var source = new VideoSource(
            Guid.NewGuid(),
            "显示器采集",
            "monitor_capture",
            null,
            null,
            new Dictionary<string, JsonElement>
            {
                ["monitor_id"] = JsonSerializer.SerializeToElement("display-1")
            },
            []);
        var snapshot = new ApplicationSnapshot(
            ApplicationKind.Obs,
            "32.2.2",
            "obs-websocket",
            string.Empty,
            "fingerprint",
            CompatibilityLevel.Verified,
            true,
            [],
            [source],
            []);

        var mappings = ObsAdapter.CreateEffectiveMappings(snapshot, [], "当前场景");

        var mapping = Assert.Single(mappings).Value;
        Assert.Equal("显示器采集", mapping.TargetSourceName);
        Assert.Equal("当前场景", mapping.TargetSceneName);
        Assert.True(mapping.CreateSourceWhenMissing);
    }

    [Fact]
    public void EffectiveMappingUpgradesLegacyLocalMappingWithoutScene()
    {
        var source = new VideoSource(
            Guid.NewGuid(),
            "采集卡",
            "dshow_input",
            null,
            null,
            new Dictionary<string, JsonElement>(),
            []);
        var snapshot = new ApplicationSnapshot(
            ApplicationKind.Obs,
            "32.2.2",
            "obs-websocket",
            string.Empty,
            "fingerprint",
            CompatibilityLevel.Verified,
            true,
            [],
            [source],
            []);
        var legacyMapping = new DeviceMapping(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            source.LogicalId,
            ApplicationKind.Obs,
            "device-id",
            "采集卡",
            string.Empty,
            false);

        var mappings = ObsAdapter.CreateEffectiveMappings(snapshot, [legacyMapping], "当前场景");

        var mapping = Assert.Single(mappings).Value;
        Assert.Equal("当前场景", mapping.TargetSceneName);
        Assert.True(mapping.CreateSourceWhenMissing);
    }
}
