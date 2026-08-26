using LiveStudio.Contracts;

namespace LiveStudio.Agent.Tests;

public sealed class LocalControlServerMappingTests
{
    [Fact]
    public void RequiresDeviceMappingExcludesNonDeviceObsSource()
    {
        var source = CreateSource(device: null);

        Assert.False(LocalControlServer.RequiresDeviceMapping(source));
    }

    [Fact]
    public void RequiresDeviceMappingIncludesHardwareSourceWithInterfaceIdentity()
    {
        var device = new CaptureDeviceDescriptor(
            "USB 采集卡",
            "1234",
            "5678",
            null,
            "usb#vid_1234&pid_5678",
            []);

        Assert.True(LocalControlServer.RequiresDeviceMapping(CreateSource(device)));
    }

    [Fact]
    public void RequiresDeviceMappingExcludesDescriptorWithoutStableInterfaceIdentity()
    {
        var device = new CaptureDeviceDescriptor("未识别设备", null, null, null, null, []);

        Assert.False(LocalControlServer.RequiresDeviceMapping(CreateSource(device)));
    }

    private static VideoSource CreateSource(CaptureDeviceDescriptor? device) => new(
        Guid.NewGuid(),
        "显示器采集",
        "monitor_capture",
        device,
        null,
        new Dictionary<string, System.Text.Json.JsonElement>(),
        []);
}
