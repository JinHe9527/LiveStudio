using System.Text;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionConfigurationStoreTests
{
    [Fact]
    public async Task CapturesAllowedVideoTreeWithoutCredentialsAndProjectsReadableParameters()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);

            var documents = await store.CaptureDocumentsAsync(CancellationToken.None);
            var sources = await LiveCompanionSnapshotProjector.CreateSourcesAsync(
                documents,
                CancellationToken.None);
            var serialized = JsonSerializer.Serialize(documents);

            Assert.Single(documents);
            Assert.DoesNotContain("secret-token", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("login", serialized, StringComparison.OrdinalIgnoreCase);
            var source = Assert.Single(sources);
            Assert.Equal("capture-card-a", source.Device?.FriendlyName);
            Assert.Equal(1920, source.Mode?.Width);
            Assert.Equal(1080, source.Mode?.Height);
            Assert.Equal(60, source.Mode?.FramesPerSecondNumerator);
            var filter = Assert.Single(source.Filters);
            Assert.Equal("人像调色", filter.Name);
            Assert.Equal("color_filter", filter.Kind);
            Assert.True(filter.Enabled);
            Assert.Single(filter.Assets);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task AppliesMappedDeviceFilterAssetsAndRestoresOriginalFileBytes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var documents = await store.CaptureDocumentsAsync(CancellationToken.None);
            var sources = await LiveCompanionSnapshotProjector.CreateSourcesAsync(
                documents,
                CancellationToken.None);
            var document = Assert.Single(documents);
            var assets = sources.SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .ToArray();
            var assetDirectory = Path.Combine(fixture.RootPath, "stable-assets");
            foreach (var asset in assets)
            {
                var directory = Path.Combine(assetDirectory, asset.BlobSha256);
                Directory.CreateDirectory(directory);
                File.Copy(asset.SourcePath, Path.Combine(directory, asset.OriginalFileName));
            }

            var backup = await store.BackupAsync(documents, CancellationToken.None);
            await File.WriteAllTextAsync(fixture.ConfigurationPath, ChangedConfiguration(fixture.LutPath));
            var mapping = new DeviceMapping(
                Guid.NewGuid(),
                Guid.Empty,
                Guid.Empty,
                document.SourceLogicalId,
                ApplicationKind.LiveCompanion,
                "capture-card-b",
                "摄像头",
                string.Empty,
                false);

            await store.ApplyAsync(
                documents,
                new Dictionary<Guid, DeviceMapping> { [document.SourceLogicalId] = mapping },
                assets,
                assetDirectory,
                CancellationToken.None);

            using (var applied = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath)))
            {
                var studio = applied.RootElement.GetProperty("studio");
                Assert.Equal("capture-card-b", studio.GetProperty("cameraId").GetString());
                Assert.Equal(1920, studio.GetProperty("width").GetInt32());
                Assert.Equal(60, studio.GetProperty("fps").GetInt32());
                var filter = studio.GetProperty("filterChain")[0];
                Assert.Equal(0.12, filter.GetProperty("contrast").GetDouble());
                Assert.StartsWith(assetDirectory, filter.GetProperty("imagePath").GetString(), StringComparison.Ordinal);
                Assert.Equal("keep-me", applied.RootElement.GetProperty("unrelated").GetString());
                Assert.Equal("secret-token", applied.RootElement.GetProperty("login").GetProperty("token").GetString());
            }

            await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, CancellationToken.None);
            var restored = await File.ReadAllBytesAsync(fixture.ConfigurationPath);
            Assert.Equal(backup[fixture.ConfigurationPath], restored);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task DetectsExplicitIdleAndLiveStates()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var idle = await store.InspectLiveStateAsync(CancellationToken.None);
            Assert.True(idle.CanDetermine);
            Assert.False(idle.IsLive);

            var liveContent = OriginalConfiguration(fixture.LutPath).Replace(
                "\"isLive\": false",
                "\"isLive\": true",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixture.ConfigurationPath, liveContent);
            var live = await store.InspectLiveStateAsync(CancellationToken.None);
            Assert.True(live.CanDetermine);
            Assert.True(live.IsLive);
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SignedDefinitionCapturesOnlyDeclaredFieldsWithExactTypes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var store = new LiveCompanionConfigurationStore(fixture.RootPath);
            var definition = new LiveCompanionAdapterDefinition(
                "fixture-adapter",
                "1.0.0",
                "1.0.0",
                new string('a', 64),
                [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    "studio.json",
                    null,
                    true)],
                [
                    new FieldMappingDefinition(
                        "device",
                        UnifiedFieldKind.DeviceSelection,
                        "main",
                        "/studio/cameraId",
                        "string",
                        true,
                        true),
                    new FieldMappingDefinition(
                        "width",
                        UnifiedFieldKind.Width,
                        "main",
                        "/studio/width",
                        "int",
                        true,
                        true)
                ],
                ["/login"],
                new LiveStateRuleDefinition("main", "/isLive", "false"),
                new ScreenshotRuleDefinition("window", "main"));
            var verified = new VerifiedAdapterDefinition(definition, "test", new string('b', 64));

            var documents = await store.CaptureDefinedDocumentsAsync(verified, CancellationToken.None);
            var liveState = await store.InspectDefinedLiveStateAsync(verified, CancellationToken.None);

            var document = Assert.Single(documents);
            LiveCompanionConfigurationStore.ValidateDefinedDocuments(verified, documents);
            Assert.Equal("fixture-adapter", document.StructureVersion);
            Assert.Equal(2, document.Values.Count);
            Assert.Contains(document.Values, value => value.JsonPointer == "/studio/cameraId");
            Assert.Contains(document.Values, value => value.JsonPointer == "/studio/width");
            Assert.DoesNotContain(document.Values, value => value.JsonPointer.Contains("login", StringComparison.OrdinalIgnoreCase));
            Assert.True(liveState.CanDetermine);
            Assert.False(liveState.IsLive);

            var injected = document with
            {
                Values = document.Values.Append(new NativeConfigurationValue(
                    "/login/token",
                    "Filter",
                    JsonSerializer.SerializeToElement("injected"))).ToArray()
            };
            Assert.Throws<InvalidOperationException>(() =>
                LiveCompanionConfigurationStore.ValidateDefinedDocuments(verified, [injected]));
        }
        finally
        {
            Directory.Delete(fixture.RootPath, recursive: true);
        }
    }

    private static async Task<LiveCompanionFixture> CreateFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"livestudio-live-companion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var lutPath = Path.Combine(root, "portrait.cube");
        var configurationPath = Path.Combine(root, "studio.json");
        await File.WriteAllTextAsync(lutPath, "LUT_3D_SIZE 2", Encoding.UTF8);
        await File.WriteAllTextAsync(configurationPath, OriginalConfiguration(lutPath), Encoding.UTF8);
        return new LiveCompanionFixture(root, configurationPath, lutPath);
    }

    private static string OriginalConfiguration(string lutPath) => $$"""
        {
          "isLive": false,
          "login": { "token": "secret-token" },
          "unrelated": "keep-me",
          "studio": {
            "cameraId": "capture-card-a",
            "width": 1920,
            "height": 1080,
            "fps": 60,
            "pixelFormat": "NV12",
            "colorSpace": "Rec.709",
            "colorRange": "Partial",
            "filterChain": [
              {
                "name": "人像调色",
                "type": "color_filter",
                "enabled": true,
                "order": 0,
                "contrast": 0.12,
                "imagePath": {{JsonSerializer.Serialize(lutPath)}}
              }
            ]
          }
        }
        """;

    private static string ChangedConfiguration(string lutPath) => OriginalConfiguration(lutPath)
        .Replace("capture-card-a", "changed-device", StringComparison.Ordinal)
        .Replace("1920", "1280", StringComparison.Ordinal)
        .Replace("0.12", "0.01", StringComparison.Ordinal);

    private sealed record LiveCompanionFixture(
        string RootPath,
        string ConfigurationPath,
        string LutPath);
}
