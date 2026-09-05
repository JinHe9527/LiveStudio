using System.Text;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionAssetMapperTests
{
    [Fact]
    public async Task CapturesExternalLutAndKeepsTheNativeReferencePath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var lutPath = Path.Combine(directory, "直播人像.cube");
            await File.WriteAllTextAsync(lutPath, "LUT_3D_SIZE 2", Encoding.UTF8);
            var tree = CreateTree(lutPath);

            var captured = await LiveCompanionAssetMapper.CaptureAsync(tree, CancellationToken.None);

            var field = Assert.Single(Assert.Single(captured.Sections).Fields);
            var asset = Assert.Single(field.Assets);
            Assert.Equal(Path.GetFullPath(lutPath), asset.SourcePath);
            Assert.Equal("直播人像.cube", asset.OriginalFileName);
            Assert.Equal("/sourceStore/camera/filterData/6/payload/file", asset.ReferencePath);
            Assert.Equal(new FileInfo(lutPath).Length, asset.Length);
            Assert.Equal(64, asset.BlobSha256.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyExternalLutFailsCaptureBeforeAnUnrestorablePackageIsCreated()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "empty.cube");
            await File.WriteAllBytesAsync(path, []);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LiveCompanionAssetMapper.CaptureAsync(CreateTree(path), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingExternalLutFailsCaptureInsteadOfSavingOnlyTheOldPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var missingPath = Path.Combine(directory, "已删除.cube");

            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                LiveCompanionAssetMapper.CaptureAsync(CreateTree(missingPath), CancellationToken.None));

            Assert.Equal(missingPath, exception.FileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedBindingMakesDeletedSourcePathRestorable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var missingPath = Path.Combine(directory, "外部色卡.cube");
            var binding = new AssetBinding(
                Guid.NewGuid(),
                new string('a', 64),
                Path.GetFileName(missingPath),
                missingPath,
                "/sourceStore/camera/filterData/6/payload/file",
                128);
            var document = CreateDocument(missingPath);

            Assert.Empty(LiveCompanionAssetMapper.FindUnresolvedAssetPaths([document], [binding]));
            Assert.Equal(
                missingPath,
                Assert.Single(LiveCompanionAssetMapper.FindUnresolvedAssetPaths([document], [])));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ConfigurationTreeSnapshot CreateTree(string lutPath)
    {
        var field = new ConfigurationFieldSnapshot(
            "live-companion:lut",
            "素材文件",
            "滤镜设置/视频滤镜链/LUT/素材文件",
            0,
            "String",
            "Text",
            JsonSerializer.SerializeToElement(lutPath),
            null,
            null,
            null,
            null,
            [],
            null,
            new NativeLocatorSnapshot(
                "JsonFile",
                "source-store",
                "/sourceStore/camera/filterData/6/payload/file",
                "sourceStore.json",
                "String"),
            FieldEvidenceStatus.Mapped,
            true,
            []);
        return new ConfigurationTreeSnapshot(
            [new ConfigurationSectionSnapshot("filter", "滤镜设置", "滤镜设置", 0, [], [field])],
            0,
            0,
            1,
            0,
            true,
            true);
    }

    private static NativeConfigurationDocument CreateDocument(string lutPath) => new(
        "source-store",
        "JsonFile",
        "json-v1",
        "sourceStore.json",
        "sourceStore.json",
        new string('b', 64),
        Guid.NewGuid(),
        [new NativeConfigurationValue(
            "/sourceStore/camera/filterData/6/payload/file",
            NativeParameterCategories.Filter,
            JsonSerializer.SerializeToElement(lutPath))]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestudio-live-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
