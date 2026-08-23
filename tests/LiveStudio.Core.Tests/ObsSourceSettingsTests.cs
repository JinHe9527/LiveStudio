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
}
