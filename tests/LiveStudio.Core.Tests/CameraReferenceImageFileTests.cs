using LiveStudio.Packaging;

namespace LiveStudio.Core.Tests;

public sealed class CameraReferenceImageFileTests
{
    [Fact]
    public void ValidateRecognizesPngAndCreatesStableStationPath()
    {
        var content = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var image = CameraReferenceImageFile.Validate(content);
        var snapshot = image.CreateSnapshot(2);

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
        Assert.Equal("camera-images/station-3.png", snapshot.PackagePath);
        Assert.Equal(image.Sha256, snapshot.Sha256);
    }

    [Fact]
    public void ValidateRejectsFilesThatOnlyHaveAnImageExtension()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            CameraReferenceImageFile.Validate("not-an-image"u8.ToArray()));

        Assert.Contains("PNG", exception.Message, StringComparison.Ordinal);
    }
}
