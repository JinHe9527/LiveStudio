using System.Text;
using System.Text.Json;
using LiveStudio.Adapters.Obs;

namespace LiveStudio.Core.Tests;

public sealed class ObsFilterAssetMapperTests
{
    [Fact]
    public async Task CapturesAndMaterializesNestedAssetsByExactSourcePath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstDirectory = Path.Combine(directory, "first");
            var secondDirectory = Path.Combine(directory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var firstPath = Path.Combine(firstDirectory, "mask.dat");
            var secondPath = Path.Combine(secondDirectory, "mask.dat");
            await File.WriteAllTextAsync(firstPath, "first", Encoding.UTF8);
            await File.WriteAllTextAsync(secondPath, "second", Encoding.UTF8);
            var settings = new Dictionary<string, JsonElement>
            {
                ["nested"] = JsonSerializer.SerializeToElement(new
                {
                    layers = new[]
                    {
                        new { file = firstPath },
                        new { file = secondPath }
                    }
                })
            };

            var assets = await ObsFilterAssetMapper.CaptureAsync(settings, CancellationToken.None);
            Assert.Equal(2, assets.Count);
            Assert.Contains(assets, asset => asset.SourcePath == firstPath);
            Assert.Contains(assets, asset => asset.SourcePath == secondPath);
            Assert.Equal(2, assets.Select(asset => asset.BlobSha256).Distinct(StringComparer.Ordinal).Count());

            var materializedDirectory = Path.Combine(directory, "materialized");
            foreach (var asset in assets)
            {
                var targetDirectory = Path.Combine(materializedDirectory, asset.BlobSha256);
                Directory.CreateDirectory(targetDirectory);
                File.Copy(asset.SourcePath, Path.Combine(targetDirectory, asset.OriginalFileName));
            }

            var materialized = ObsFilterAssetMapper.Materialize(settings, assets, materializedDirectory);
            var paths = materialized["nested"]
                .GetProperty("layers")
                .EnumerateArray()
                .Select(layer => layer.GetProperty("file").GetString())
                .ToArray();

            Assert.Equal(Path.Combine(materializedDirectory, assets[0].BlobSha256, "mask.dat"), paths[0]);
            Assert.Equal(Path.Combine(materializedDirectory, assets[1].BlobSha256, "mask.dat"), paths[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsMissingMaterializedAssetInsteadOfKeepingStalePath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "rollback.cube");
            await File.WriteAllTextAsync(sourcePath, "LUT_3D_SIZE 2", Encoding.UTF8);
            var settings = new Dictionary<string, JsonElement>
            {
                ["image_path"] = JsonSerializer.SerializeToElement(sourcePath)
            };
            var assets = await ObsFilterAssetMapper.CaptureAsync(settings, CancellationToken.None);

            Assert.Throws<FileNotFoundException>(() => ObsFilterAssetMapper.Materialize(
                settings,
                assets,
                Path.Combine(directory, "missing-assets")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsSeparateBindingsForIdenticalContentWithDifferentFileNames()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "portrait.cube");
            var secondPath = Path.Combine(directory, "product.cube");
            await File.WriteAllTextAsync(firstPath, "LUT_3D_SIZE 2", Encoding.UTF8);
            await File.WriteAllTextAsync(secondPath, "LUT_3D_SIZE 2", Encoding.UTF8);
            var settings = new Dictionary<string, JsonElement>
            {
                ["portrait_path"] = JsonSerializer.SerializeToElement(firstPath),
                ["product_path"] = JsonSerializer.SerializeToElement(secondPath)
            };

            var bindings = await ObsFilterAssetMapper.CaptureAsync(settings, CancellationToken.None);

            Assert.Equal(2, bindings.Count);
            Assert.Single(bindings.Select(binding => binding.BlobSha256).Distinct(StringComparer.Ordinal));
            Assert.Equal(2, bindings.Select(binding => binding.Id).Distinct().Count());
            Assert.Contains(bindings, binding => binding.OriginalFileName == "portrait.cube");
            Assert.Contains(bindings, binding => binding.OriginalFileName == "product.cube");

            var materializedDirectory = Path.Combine(directory, "materialized");
            foreach (var binding in bindings)
            {
                var targetDirectory = Path.Combine(materializedDirectory, binding.BlobSha256);
                Directory.CreateDirectory(targetDirectory);
                File.Copy(binding.SourcePath, Path.Combine(targetDirectory, binding.OriginalFileName));
            }

            var materialized = ObsFilterAssetMapper.Materialize(settings, bindings, materializedDirectory);
            Assert.EndsWith("portrait.cube", materialized["portrait_path"].GetString(), StringComparison.Ordinal);
            Assert.EndsWith("product.cube", materialized["product_path"].GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacesMissingKnownColorCardBeforeSnapshotCapture()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var builtInPath = Path.Combine(directory, "发灰提亮.png");
            await File.WriteAllBytesAsync(builtInPath, [137, 80, 78, 71]);
            var stalePath = @"D:\调试\旧色卡索引\发灰提亮.png";
            var settings = new Dictionary<string, JsonElement>
            {
                ["image_path"] = JsonSerializer.SerializeToElement(stalePath)
            };
            var resolver = new DictionaryAssetResolver(new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["发灰提亮.png"] = builtInPath
            });

            var resolved = ObsFilterAssetMapper.ResolveMissingAssets(
                settings,
                resolver,
                rejectUnresolved: true);
            var assets = await ObsFilterAssetMapper.CaptureAsync(resolved, CancellationToken.None);

            Assert.Equal(builtInPath, resolved["image_path"].GetString());
            Assert.Equal(builtInPath, Assert.Single(assets).SourcePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MaterializesLegacySnapshotWithoutBindingFromBuiltInColorCard()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var builtInPath = Path.Combine(directory, "肤色红润.png");
            File.WriteAllBytes(builtInPath, [137, 80, 78, 71]);
            var stalePath = @"D:\不存在\色卡\肤色红润.png";
            var settings = new Dictionary<string, JsonElement>
            {
                ["image_path"] = JsonSerializer.SerializeToElement(stalePath)
            };
            var resolver = new DictionaryAssetResolver(new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["肤色红润.png"] = builtInPath
            });

            var materialized = ObsFilterAssetMapper.Materialize(
                settings,
                [],
                Path.Combine(directory, "snapshot-assets"),
                resolver);

            Assert.Equal(builtInPath, materialized["image_path"].GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsMissingKnownAssetWhenSnapshotAndBuiltInBundleCannotProvideIt()
    {
        var stalePath = @"D:\不存在\色卡\未收录.png";
        var settings = new Dictionary<string, JsonElement>
        {
            ["image_path"] = JsonSerializer.SerializeToElement(stalePath)
        };

        var exception = Assert.Throws<FileNotFoundException>(() =>
            ObsFilterAssetMapper.ResolveMissingAssets(settings, null, rejectUnresolved: true));

        Assert.Equal(stalePath, exception.FileName);
    }

    private sealed class DictionaryAssetResolver(IReadOnlyDictionary<string, string> paths)
        : IObsAssetPathResolver
    {
        public string? ResolveMissingPath(string configuredPath) =>
            paths.TryGetValue(Path.GetFileName(configuredPath), out var path) ? path : null;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestudio-obs-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
