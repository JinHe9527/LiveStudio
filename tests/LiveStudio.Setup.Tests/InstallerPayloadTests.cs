using System.IO.Compression;
using System.Text;
using LiveStudio.Setup;

namespace LiveStudio.Setup.Tests;

public sealed class InstallerPayloadTests
{
    [Fact]
    public void ParseChecksumAcceptsReleaseFormat()
    {
        var hash = new string('a', 64);

        var result = InstallerPayload.ParseChecksum($"{hash}  LiveStudio-Windows-x64.msix\r\n");

        Assert.Equal(hash.ToUpperInvariant(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234  package.msix")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseChecksumRejectsInvalidContent(string content)
    {
        Assert.Throws<InvalidDataException>(() => InstallerPayload.ParseChecksum(content));
    }

    [Fact]
    public void ReadPackageIdentityReturnsVersionForExpectedPublisher()
    {
        var path = CreatePackage("CN=LiveStudio Internal", "1.2.3.0");
        try
        {
            var version = InstallerPayload.ReadPackageIdentity(path);

            Assert.Equal(new Version(1, 2, 3, 0), version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPackageIdentityRejectsUnexpectedPublisher()
    {
        var path = CreatePackage("CN=Unexpected", "1.2.3.0");
        try
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => InstallerPayload.ReadPackageIdentity(path));

            Assert.Contains("Publisher 不匹配", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AuthenticodeTrustVerifierRejectsUnsignedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LiveStudio-Unsigned-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "unsigned");

            var exception = Assert.Throws<InvalidDataException>(
                () => AuthenticodeTrustVerifier.Verify(path));

            Assert.Contains("签名信任校验失败", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreatePackage(string publisher, string version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"LiveStudio-Setup-Test-{Guid.NewGuid():N}.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("AppxManifest.xml");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write($$"""
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="LiveStudio.BroadcastConfiguration"
            Publisher="{{publisher}}"
            Version="{{version}}" />
</Package>
""");
        return path;
    }
}
