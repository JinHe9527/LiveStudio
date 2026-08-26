using System.IO.Compression;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionEffectPackageTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreatesNativeEffectArchiveWithExpectedConfigurationAndSignature()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"livestudio-effect-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "effect.zip");
            var document = new NativeConfigurationDocument(
                "effect-config",
                "JsonFile",
                "test",
                @"WBStore\effectConfigStore.json",
                @"WBStore\effectConfigStore.json",
                "hash",
                Guid.NewGuid(),
                [
                    new NativeConfigurationValue(
                        "/effectConfigStore/configs/config-1/effect/items/item-1/sliders/Smooth/value",
                        "Beauty",
                        JsonSerializer.SerializeToElement(0.33)),
                    new NativeConfigurationValue(
                        "/effectConfigStore/configs/config-1/effect/items/item-1/sliders/Smooth/use",
                        "Beauty",
                        JsonSerializer.SerializeToElement(true))
                ]);

            await LiveCompanionEffectPackage.CreateAsync(path, document, "config-1", CancellationToken.None);

            using var archive = ZipFile.OpenRead(path);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("config.json", entry.FullName);
            using var json = JsonDocument.Parse(entry.Open());
            var root = json.RootElement;
            var content = root.GetProperty("content").GetString();
            Assert.NotNull(content);
            using var contentJson = JsonDocument.Parse(content);
            Assert.Equal(
                0.33,
                contentJson.RootElement
                    .GetProperty("config")
                    .GetProperty("effect")
                    .GetProperty("items")
                    .GetProperty("item-1")
                    .GetProperty("sliders")
                    .GetProperty("Smooth")
                    .GetProperty("value")
                    .GetDouble());
            Assert.Empty(root.GetProperty("files").EnumerateArray());
            var contentHash = LiveCompanionEffectPackage.Sha1Hex(content);
            var expectedSignature = LiveCompanionEffectPackage.Sha1Hex(
                JsonSerializer.Serialize(new[] { contentHash }, JsonOptions));
            Assert.Equal(expectedSignature, root.GetProperty("sign").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
