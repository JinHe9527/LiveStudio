using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Packaging;

namespace LiveStudio.Agent.Tests;

public sealed class LocalRestoreAssetValidationTests
{
    [Fact]
    public void ValidateAssetEntriesComparesBindingLengthWithBlobLength()
    {
        var content = "same LUT bytes"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var asset = new AssetBlob(hash, "application/octet-stream", content.Length, $"assets/{hash}/content");
        var filter = new VideoFilter(
            Guid.NewGuid(),
            "应用 LUT",
            "clut_filter",
            true,
            0,
            new Dictionary<string, System.Text.Json.JsonElement>(),
            [new AssetBinding(Guid.NewGuid(), hash, "色卡.cube", "C:\\色卡.cube", "/lut", content.Length)]);
        var source = new VideoSource(
            Guid.NewGuid(),
            "采集卡",
            "dshow_input",
            null,
            null,
            new Dictionary<string, System.Text.Json.JsonElement>(),
            [filter]);
        var application = new ApplicationSnapshot(
            ApplicationKind.Obs,
            "32.2.2",
            "obs-websocket-5",
            new string('a', 64),
            new string('b', 64),
            CompatibilityLevel.Verified,
            true,
            [],
            [source],
            []);
        var snapshot = new CombinedSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "素材校验",
            DateTimeOffset.UtcNow,
            3,
            [application],
            [asset],
            []);
        var package = new SnapshotPackage(
            new SnapshotPackageManifest(
                snapshot.Id,
                snapshot.OrganizationId,
                snapshot.RoomId,
                snapshot.Name,
                snapshot.CreatedAt,
                snapshot.SchemaVersion,
                [],
                [],
                []),
            snapshot,
            new Dictionary<string, PackageFile>
            {
                [asset.PackagePath] = new(asset.PackagePath, asset.MediaType, content)
            });

        LocalRestoreService.ValidateAssetEntries(package);
    }
}
