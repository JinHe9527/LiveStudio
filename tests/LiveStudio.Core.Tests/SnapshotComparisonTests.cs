using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotComparisonTests
{
    [Fact]
    public void ReportsVideoModeAndFilterSettingDifferences()
    {
        var sourceId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var left = CreateDetail(sourceId, filterId, 30, true, 0.45);
        var right = CreateDetail(sourceId, filterId, 60, false, 0.8);

        var differences = SnapshotParameterComparer.Compare(left, right);

        Assert.Contains(differences, value =>
            value.Label.Contains("画面模式", StringComparison.Ordinal)
            && value.LeftValue.Contains("30 FPS", StringComparison.Ordinal)
            && value.RightValue.Contains("60 FPS", StringComparison.Ordinal));
        Assert.Contains(differences, value =>
            value.Label.Contains("状态", StringComparison.Ordinal)
            && value.LeftValue == "启用"
            && value.RightValue == "停用");
        Assert.Contains(differences, value =>
            value.Label.EndsWith("不透明度", StringComparison.Ordinal)
            && value.LeftValue == "0.45（45%）"
            && value.RightValue == "0.8（80%）");
    }

    [Fact]
    public void PresentsTechnicalSnapshotValuesAsReadableChinese()
    {
        var filterSettings = new Dictionary<string, JsonElement>
        {
            ["brightness"] = JsonSerializer.SerializeToElement(0.12),
            ["image_path"] = JsonSerializer.SerializeToElement(@"C:\LiveStudio\Assets\grade.cube")
        };

        var presented = SnapshotParameterPresentation.PresentFilterSettings(filterSettings);

        Assert.Contains(presented, value =>
            value.Name == "亮度"
            && value.Value == "0.12（12%）"
            && value.TechnicalName == "brightness");
        Assert.Contains(presented, value =>
            value.Name == "素材文件"
            && value.Value.EndsWith(@"C:\LiveStudio\Assets\grade.cube", StringComparison.Ordinal)
            && value.TechnicalName == "image_path");
        Assert.Equal("有限范围（Partial）", SnapshotParameterPresentation.ColorRange("Limited"));
        Assert.Equal("LUT 调色", SnapshotParameterPresentation.FilterKindName("clut_filter"));
        var nativeSourceSettings = SnapshotParameterPresentation.PresentSourceSettings(
            new Dictionary<string, JsonElement>
            {
                ["/studio/cameraId"] = JsonSerializer.SerializeToElement("capture-card-a")
            });
        var nativeDevice = Assert.Single(nativeSourceSettings);
        Assert.Equal("设备接口标识", nativeDevice.Name);
        Assert.Equal("/studio/cameraId", nativeDevice.TechnicalName);
    }

    [Fact]
    public void ReturnsNoDifferencesForEquivalentSnapshots()
    {
        var sourceId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var left = CreateDetail(sourceId, filterId, 30, true, 0.45);
        var right = CreateDetail(sourceId, filterId, 30, true, 0.45);

        Assert.Empty(SnapshotParameterComparer.Compare(left, right));
    }

    private static SnapshotDetail CreateDetail(
        Guid sourceId,
        Guid filterId,
        int framesPerSecond,
        bool filterEnabled,
        double opacity)
    {
        var summary = new SnapshotSummary(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "测试存档",
            DateTimeOffset.UtcNow,
            1024,
            new string('a', 64));
        var device = new CaptureDeviceDescriptor(
            "采集卡",
            "1234",
            "5678",
            "SERIAL",
            null,
            []);
        var mode = new VideoMode(
            1920,
            1080,
            framesPerSecond,
            1,
            "NV12",
            "Rec.709",
            "Limited");
        var filter = new VideoFilter(
            filterId,
            "色彩校正",
            "color_filter_v2",
            filterEnabled,
            0,
            new Dictionary<string, JsonElement>
            {
                ["opacity"] = JsonSerializer.SerializeToElement(opacity)
            },
            []);
        var source = new VideoSource(
            sourceId,
            "主摄像头",
            "dshow_input",
            device,
            mode,
            new Dictionary<string, JsonElement>(),
            [filter]);
        var application = new ApplicationSnapshot(
            ApplicationKind.Obs,
            "31.1.2",
            "test-adapter",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            "fingerprint",
            CompatibilityLevel.Verified,
            true,
            [],
            [source],
            []);
        return new SnapshotDetail(summary, [application], new Dictionary<ApplicationKind, Uri>());
    }
}
