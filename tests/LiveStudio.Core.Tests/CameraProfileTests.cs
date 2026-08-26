using LiveStudio.Desktop.Services;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class CameraProfileTests
{
    private static readonly CameraCreativeLookOptionViewModel[] CreativeLooks =
    [
        new("ST", "标准"),
        new("PT", "人像")
    ];

    [Fact]
    public void ThreeCameraStationsUseRequestedDefaults()
    {
        var stations = new[]
        {
            new CameraStationEditorViewModel(0, "主机", CreativeLooks),
            new CameraStationEditorViewModel(1, "游机", CreativeLooks),
            new CameraStationEditorViewModel(2, "侧机", CreativeLooks)
        };

        Assert.Equal(["主机", "游机", "侧机"], stations.Select(station => station.Name));
        Assert.All(stations, station =>
        {
            Assert.Equal("F4", station.Aperture);
            Assert.Equal("1/125", station.ShutterSpeed);
            Assert.Equal("640", station.Iso);
            Assert.Equal("ST", station.SelectedCreativeLook?.Code);
            Assert.True(station.TryGetCreativeLookSettings(out var settings, out var error), error);
            Assert.Equal(CameraCreativeLookSettings.StandardDefault, settings);
            Assert.Equal(new CameraCreativeLookSettings(0, 0, 0, 0, 0, 0, 0, 0), settings);
        });
    }

    [Fact]
    public void CameraStationCreatesSnapshotWithAllManualFields()
    {
        var station = new CameraStationEditorViewModel(1, "游机", CreativeLooks);

        Assert.True(station.TryCreateSnapshot(out var snapshot, out var error), error);
        Assert.Equal(1, snapshot.Slot);
        Assert.Equal("游机", snapshot.Name);
        Assert.Equal("F4", snapshot.Aperture);
        Assert.Equal("1/125", snapshot.ShutterSpeed);
        Assert.Equal("640", snapshot.Iso);
        Assert.Equal("ST", snapshot.CreativeLook);
        Assert.Equal(new LiveStudio.Contracts.CameraCreativeLookSnapshot(0, 0, 0, 0, 0, 0, 0, 0), snapshot.CreativeLookSettings);
    }

    [Fact]
    public void CameraStationRejectsInvalidDirectEntry()
    {
        var station = new CameraStationEditorViewModel(0, "主机", CreativeLooks)
        {
            Contrast = "10"
        };

        Assert.False(station.TryGetCreativeLookSettings(out _, out var error));
        Assert.Equal("对比度请输入 -9–9 的整数", error);
    }

    [Fact]
    public void ManualInputNormalizesExposureAndCreativeLook()
    {
        var valid = CameraProfileInput.TryNormalize(
            " 3号直播间主机位 ",
            "2.8",
            "1/50",
            "ISO 800",
            "pt",
            out var values,
            out var error);

        Assert.True(valid, error);
        Assert.Equal("3号直播间主机位", values.Name);
        Assert.Equal("F2.8", values.Aperture);
        Assert.Equal("1/50", values.ShutterSpeed);
        Assert.Equal("800", values.Iso);
        Assert.Equal("PT", values.CreativeLook);
        Assert.Equal(CameraCreativeLookSettings.StandardDefault, values.CreativeLookSettings);
    }

    [Fact]
    public void ManualInputPreservesAllCreativeLookAdjustments()
    {
        var settings = new CameraCreativeLookSettings(-2, -4, 1, 3, -1, 5, 2, 4);

        var valid = CameraProfileInput.TryNormalize(
            "主机位",
            "F4",
            "1/125",
            "640",
            "ST",
            settings,
            out var values,
            out var error);

        Assert.True(valid, error);
        Assert.Equal(settings, values.CreativeLookSettings);
    }

    [Theory]
    [InlineData(-10, 0, 0, 0, 0, 4, 3, 1)]
    [InlineData(0, 10, 0, 0, 0, 4, 3, 1)]
    [InlineData(0, 0, 0, -1, 0, 4, 3, 1)]
    [InlineData(0, 0, 0, 0, 0, 10, 3, 1)]
    [InlineData(0, 0, 0, 0, 0, 4, -1, 1)]
    [InlineData(0, 0, 0, 0, 0, 4, 3, 10)]
    public void ManualInputRejectsOutOfRangeCreativeLookAdjustments(
        int contrast,
        int highlights,
        int shadows,
        int fade,
        int saturation,
        int sharpness,
        int sharpnessRange,
        int clarity)
    {
        var settings = new CameraCreativeLookSettings(
            contrast,
            highlights,
            shadows,
            fade,
            saturation,
            sharpness,
            sharpnessRange,
            clarity);

        Assert.False(CameraProfileInput.TryNormalize(
            "主机位",
            "F4",
            "1/125",
            "640",
            "ST",
            settings,
            out _,
            out var error));
        Assert.Contains("超出", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("F0.5", "1/50", "800", "ST")]
    [InlineData("F2.8", "fast", "800", "ST")]
    [InlineData("F2.8", "1/50", "999999", "ST")]
    [InlineData("F2.8", "1/50", "800", "UNKNOWN")]
    public void ManualInputRejectsInvalidCameraValues(
        string aperture,
        string shutter,
        string iso,
        string creativeLook)
    {
        Assert.False(CameraProfileInput.TryNormalize(
            "主机位",
            aperture,
            shutter,
            iso,
            creativeLook,
            out _,
            out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task CameraProfileStoreRoundTripsProfilesAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"livestudio-camera-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "camera-profiles.json");
        try
        {
            var store = new CameraProfileStore(path);
            var profile = new CameraProfile(
                Guid.NewGuid(),
                "晚场主机位",
                DateTimeOffset.UtcNow,
                CameraProfileMode.Manual,
                "F2.8",
                "1/50",
                "800",
                "PT",
                CreativeLookSettings: new CameraCreativeLookSettings(-1, 2, 3, 4, -5, 6, 2, 8),
                StationSlot: 1);

            await store.SaveAsync([profile], CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            var saved = Assert.Single(loaded);
            Assert.Equal(profile.Id, saved.Id);
            Assert.Equal("PT", saved.CreativeLook);
            Assert.Equal(profile.CreativeLookSettings, saved.CreativeLookSettings);
            Assert.Equal(1, saved.StationSlot);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
