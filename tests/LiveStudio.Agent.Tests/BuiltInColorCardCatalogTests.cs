using LiveStudio.Agent;

namespace LiveStudio.Agent.Tests;

public sealed class BuiltInColorCardCatalogTests
{
    [Fact]
    public async Task ValidatesCompleteBundleAndResolvesLegacyPathByFileName()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "BuiltInAssets", "ColorCards", "v1");
        var catalog = new BuiltInColorCardCatalog(root);

        await catalog.EnsureIntegrityAsync(CancellationToken.None);

        var resolved = catalog.ResolveMissingPath(@"D:\调试\校色方案\色卡\发灰提亮.png");
        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.Equal("发灰提亮.png", Path.GetFileName(resolved));
        Assert.Null(catalog.ResolveMissingPath(@"D:\调试\校色方案\色卡\不存在.png"));
    }

    [Fact]
    public async Task RejectsBundleWhenManifestWasChanged()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(
                AppContext.BaseDirectory,
                "BuiltInAssets",
                "ColorCards",
                "v1",
                "manifest.json");
            var content = await File.ReadAllTextAsync(source);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "manifest.json"),
                content.Replace("color-cards-v1", "color-cards-v2", StringComparison.Ordinal));
            var catalog = new BuiltInColorCardCatalog(directory);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                catalog.EnsureIntegrityAsync(CancellationToken.None));

            Assert.Contains("清单哈希", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsBundleWhenManifestIsValidButFilesAreMissing()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(
                AppContext.BaseDirectory,
                "BuiltInAssets",
                "ColorCards",
                "v1",
                "manifest.json");
            File.Copy(source, Path.Combine(directory, "manifest.json"));
            var catalog = new BuiltInColorCardCatalog(directory);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                catalog.EnsureIntegrityAsync(CancellationToken.None));

            Assert.Contains("文件缺失", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeploysCompleteObsLibraryAndRepairsChangedMaterial()
    {
        var destination = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "BuiltInAssets", "ColorCards", "v1");
            var catalog = new BuiltInColorCardCatalog(source, destination);

            await catalog.EnsureIntegrityAsync(CancellationToken.None);

            Assert.Equal(
                BuiltInColorCardCatalog.ExpectedAssetCount,
                Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
            var resolved = catalog.ResolveMissingPath(Path.Combine(destination, "旧索引", "2.png"));
            var resolvedPath = Assert.IsType<string>(resolved);
            Assert.StartsWith(destination, resolvedPath, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(resolvedPath, "changed");

            var repaired = catalog.ResolveMissingPath(@"D:\丢失的索引\2.png");
            var repairedPath = Assert.IsType<string>(repaired);

            Assert.Equal(resolvedPath, repairedPath);
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(source, "png", "2.png")),
                await File.ReadAllBytesAsync(repairedPath));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestudio-color-card-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
