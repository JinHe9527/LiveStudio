using System.IO.Compression;
using System.Text.Json;
using LiveStudio.Discovery.Windows;

namespace LiveStudio.Core.Tests;

public sealed class NativeExportInspectorTests
{
    [Fact]
    public async Task InspectExportEnumeratesBeautyCurveLeavesWithoutSavingValues()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "beauty.zip");
            CreateExport(path, 0.62, "private-token");

            var report = await NativeExportInspector.InspectAsync(
                "美颜曲线",
                path,
                CancellationToken.None);

            var entry = Assert.Single(report.Entries);
            Assert.Equal("JSON", entry.StorageFormat);
            Assert.Contains(entry.Fields, field => field.JsonPointer == "/beauty/smooth");
            Assert.Contains(entry.Fields, field => field.JsonPointer == "/beauty/curve/master/1/y");
            Assert.Contains("config.json:/auth/token", report.SensitivePaths);
            var serialized = JsonSerializer.Serialize(report);
            Assert.DoesNotContain("private-token", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("0.62", serialized, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiffExportIdentifiesSingleBeautyParameterChange()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var beforePath = Path.Combine(directory, "before.zip");
            var afterPath = Path.Combine(directory, "after.zip");
            CreateExport(beforePath, 0.45, "same-token");
            CreateExport(afterPath, 0.72, "same-token");
            var before = await NativeExportInspector.InspectAsync("before", beforePath, CancellationToken.None);
            var after = await NativeExportInspector.InspectAsync("after", afterPath, CancellationToken.None);

            var difference = NativeExportReportComparer.Compare(before, after);

            Assert.Contains("config.json:/beauty/smooth", difference.ChangedFields);
            Assert.DoesNotContain(
                difference.ChangedFields,
                path => path.Contains("curve", StringComparison.Ordinal));
            Assert.Contains("config.json", difference.ChangedEntries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectExportRejectsTraversalEntry()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "unsafe.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../config.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("{}");
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                NativeExportInspector.InspectAsync("unsafe", path, CancellationToken.None));

            Assert.Contains("路径不安全", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectExportRejectsCorruptedArchive()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "corrupted.zip");
            await File.WriteAllTextAsync(path, "not-a-zip");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                NativeExportInspector.InspectAsync("corrupted", path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateExport(string path, double smooth, string token)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("config.json", CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            beauty = new
            {
                enabled = true,
                smooth,
                curve = new
                {
                    master = new[]
                    {
                        new { x = 0.0, y = 0.0 },
                        new { x = 0.5, y = 0.6 },
                        new { x = 1.0, y = 1.0 }
                    }
                }
            },
            auth = new { token }
        });
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestudio-native-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
