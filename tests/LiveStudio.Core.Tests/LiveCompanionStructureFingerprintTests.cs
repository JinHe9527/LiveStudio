using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionStructureFingerprintTests
{
    [Fact]
    public void ParameterValueChangesDoNotChangeFingerprint()
    {
        var first = CreateDocument("camera-a", 1920);
        var second = CreateDocument("camera-b", 1280);

        Assert.Equal(
            LiveCompanionStructureFingerprint.Compute([first]),
            LiveCompanionStructureFingerprint.Compute([second]));
    }

    [Fact]
    public void NativePathOrTypeChangesChangeFingerprint()
    {
        var original = CreateDocument("camera-a", 1920);
        var changedPath = original with
        {
            Values =
            [
                original.Values[0] with { JsonPointer = "/video/deviceId" },
                original.Values[1]
            ]
        };
        var changedType = original with
        {
            Values =
            [
                original.Values[0],
                original.Values[1] with { Value = JsonSerializer.SerializeToElement("1920") }
            ]
        };

        var fingerprint = LiveCompanionStructureFingerprint.Compute([original]);
        Assert.NotEqual(fingerprint, LiveCompanionStructureFingerprint.Compute([changedPath]));
        Assert.NotEqual(fingerprint, LiveCompanionStructureFingerprint.Compute([changedType]));
    }

    private static NativeConfigurationDocument CreateDocument(string deviceId, int width) => new(
        "main",
        "JsonFile",
        "1",
        "studio.json",
        "studio.json",
        new string('0', 64),
        Guid.Parse("65ff7b63-d648-4516-9ee2-e524204c8f56"),
        [
            new NativeConfigurationValue(
                "/video/device",
                "device",
                JsonSerializer.SerializeToElement(deviceId)),
            new NativeConfigurationValue(
                "/video/width",
                "width",
                JsonSerializer.SerializeToElement(width))
        ]);
}
