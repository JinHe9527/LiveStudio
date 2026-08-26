using LiveStudio.Agent;
using LiveStudio.Contracts;

namespace LiveStudio.Agent.Tests;

public sealed class CameraSnapshotTests
{
    [Fact]
    public void MissingCameraInputCreatesThreeRequestedDefaults()
    {
        var stations = SnapshotCaptureService.NormalizeCameraStations(null);

        Assert.Equal(["主机", "游机", "侧机"], stations.Select(station => station.Name));
        Assert.All(stations, station =>
        {
            Assert.Equal("F4", station.Aperture);
            Assert.Equal("1/125", station.ShutterSpeed);
            Assert.Equal("640", station.Iso);
            Assert.Equal("ST", station.CreativeLook);
            Assert.Equal(new CameraCreativeLookSnapshot(0, 0, 0, 0, 0, 0, 0, 0), station.CreativeLookSettings);
        });
    }

    [Fact]
    public void IncompleteCameraInputIsRejectedBeforePackageWrite()
    {
        var station = new CameraStationSnapshot(
            0,
            "主机",
            "F4",
            "1/125",
            "640",
            "ST",
            new CameraCreativeLookSnapshot(0, 0, 0, 0, 0, 0, 0, 0));

        var exception = Assert.Throws<SnapshotCaptureException>(() =>
            SnapshotCaptureService.NormalizeCameraStations([station]));

        Assert.Contains("三个机位", exception.Message, StringComparison.Ordinal);
    }
}
