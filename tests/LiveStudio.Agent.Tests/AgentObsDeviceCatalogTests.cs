using System.Text.Json;
using LiveStudio.Agent;

namespace LiveStudio.Agent.Tests;

public sealed class AgentObsDeviceCatalogTests
{
    [Fact]
    public void ParseVideoDevicesKeepsEnabledUniqueObsIdentifiers()
    {
        using var json = JsonDocument.Parse(
            """
            [
              { "itemName": "天创恒达 4K Pro", "itemValue": "device-b", "itemEnabled": true },
              { "itemName": "美乐威 4K Pro", "itemValue": "device-a", "itemEnabled": true },
              { "itemName": "重复项", "itemValue": "device-a", "itemEnabled": true },
              { "itemName": "当前不可用", "itemValue": "device-c", "itemEnabled": false }
            ]
            """);

        var devices = AgentObsDeviceCatalog.ParseVideoDevices(json.RootElement);

        Assert.Collection(
            devices,
            device =>
            {
                Assert.Equal("device-b", device.DeviceId);
                Assert.Equal("天创恒达 4K Pro", device.FriendlyName);
            },
            device =>
            {
                Assert.Equal("device-a", device.DeviceId);
                Assert.Equal("美乐威 4K Pro", device.FriendlyName);
            });
    }
}
