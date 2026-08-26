using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class RestoreMappingMatcherTests
{
    [Fact]
    public void ExactSourceNameIsMatchedWithinSameApplication()
    {
        var source = Source(ApplicationKind.Obs, "主机位", "采集卡 A");
        var expected = Target(ApplicationKind.Obs, "主机位", "device-b", "采集卡 B");

        var result = RestoreMappingMatcher.FindAutomaticTarget(
            source,
            [
                Target(ApplicationKind.LiveCompanion, "主机位", "live-device", "采集卡 A"),
                expected
            ]);

        Assert.Same(expected, result);
    }

    [Fact]
    public void UniqueDeviceNameIsUsedWhenSourceNameChanged()
    {
        var source = Source(ApplicationKind.LiveCompanion, "旧名称", "OBS Virtual Camera");
        var expected = Target(
            ApplicationKind.LiveCompanion,
            "新名称",
            "virtual-camera",
            "OBS Virtual Camera");

        var result = RestoreMappingMatcher.FindAutomaticTarget(source, [expected]);

        Assert.Same(expected, result);
    }

    [Fact]
    public void AmbiguousCandidatesRequireUserChoice()
    {
        var source = Source(ApplicationKind.Obs, "旧来源", "同型号采集卡");

        var result = RestoreMappingMatcher.FindAutomaticTarget(
            source,
            [
                Target(ApplicationKind.Obs, "来源一", "device-1", "同型号采集卡"),
                Target(ApplicationKind.Obs, "来源二", "device-2", "同型号采集卡")
            ]);

        Assert.Null(result);
    }

    private static LocalMappingSourceItemViewModel Source(
        ApplicationKind application,
        string sourceName,
        string deviceName) => new(new LocalMappingSource(
        Guid.NewGuid(),
        application,
        sourceName,
        deviceName,
        new VideoMode(1920, 1080, 30, 1, "NV12", "709", "Limited"),
        null));

    private static LocalMappingTargetItemViewModel Target(
        ApplicationKind application,
        string sourceName,
        string deviceId,
        string deviceName) => new(new LocalMappingTarget(
        application,
        sourceName,
        deviceId,
        deviceName,
        new VideoMode(1920, 1080, 30, 1, "NV12", "709", "Limited")));
}
