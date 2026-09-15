using System.IO.Compression;
using System.Text.Json;
using LiveStudio.Diagnostics;

namespace LiveStudio.Core.Tests;

public sealed class DiagnosticPackageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LiveStudio-Package-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmptyLogsStillProduceUsefulPackageWithoutAgentOrNetwork()
    {
        var package = DiagnosticPackage.Create(new DiagnosticOutbox(directory));
        var files = Read(package);
        Assert.Equal(3, files.Count);
        Assert.Equal(0, package.ErrorKinds);
        Assert.Equal("[]", files["errors.json"]);
        Assert.Contains("没有错误记录不代表软件已通过测试", files["说明.txt"], StringComparison.Ordinal);
        using var summary = JsonDocument.Parse(files["summary.json"]);
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("compatibility").ValueKind);
        Assert.Empty(summary.RootElement.GetProperty("applications").EnumerateArray());
    }

    [Fact]
    public void PackageIncludesAllComponentsAndMergedCountsWithoutClearingLogs()
    {
        var outbox = new DiagnosticOutbox(directory);
        foreach (var component in new[] { "Desktop", "Agent", "Setup" })
            outbox.Enqueue(DiagnosticReportFactory.Create(component, "Capture", new IOException("private")));
        outbox.Enqueue(DiagnosticReportFactory.Create("Desktop", "Capture", new IOException("different private message")));
        var package = DiagnosticPackage.Create(outbox);
        var files = Read(package);
        using var document = JsonDocument.Parse(files["errors.json"]);
        Assert.Equal(3, document.RootElement.GetArrayLength());
        Assert.Equal(2, document.RootElement.EnumerateArray().Single(item => item.GetProperty("component").GetString() == "Desktop")
            .GetProperty("occurrences").GetInt32());
        Assert.Equal(3, outbox.ReadSnapshot().Reports.Length);
        Assert.Equal(3, outbox.GetStatus().Pending);
    }

    [Fact]
    public void ExportReserializesKnownFieldsAndNeverCopiesRawFilesOrExtraJsonProperties()
    {
        var outbox = new DiagnosticOutbox(directory);
        outbox.Enqueue(DiagnosticReportFactory.Create("Desktop", "Capture", new IOException(@"C:\Users\SecretUser\token=private-value")));
        var file = Directory.GetFiles(directory, "*.json").Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"report\":{", "\"report\":{\"rawSecret\":\"private-value\",", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(directory, "install.log"), "private-value");
        File.WriteAllText(Path.Combine(directory, "config.json"), "{\"account\":\"private-value\"}");
        var files = Read(DiagnosticPackage.Create(outbox));
        Assert.Equal(3, files.Count);
        foreach (var content in files.Values)
        {
            Assert.DoesNotContain("private-value", content, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretUser", content, StringComparison.Ordinal);
            Assert.DoesNotContain("rawSecret", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CorruptAndOversizedRecordsAreReportedAsSkippedWhileValidErrorsRemain()
    {
        var outbox = new DiagnosticOutbox(directory);
        outbox.Enqueue(DiagnosticReportFactory.Create("Agent", "Restore", new IOException()));
        File.WriteAllText(Path.Combine(directory, "corrupt.json"), "{");
        File.WriteAllText(Path.Combine(directory, "oversize.json"), new string('x', 40_000));
        var package = DiagnosticPackage.Create(outbox);
        Assert.Equal(1, package.ErrorKinds);
        Assert.Equal(2, package.SkippedFiles);
        using var summary = JsonDocument.Parse(Read(package)["summary.json"]);
        Assert.Equal(2, summary.RootElement.GetProperty("skippedLogFiles").GetInt32());
    }

    [Fact]
    public void CompatibilityAndVersionsAreProjectedWithoutFreeText()
    {
        var package = DiagnosticPackage.Create(new DiagnosticOutbox(directory),
            [new("Obs", "32.1.0", true, true), new("LiveCompanion", "private-token", true, true)],
            new(DateTimeOffset.UtcNow, "private-token", "private-token", "private-token", -1, -1));
        var json = Read(package)["summary.json"];
        Assert.DoesNotContain("private-token", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("32.1.0", root.GetProperty("applications")[0].GetProperty("version").GetString());
        Assert.Equal("0.0", root.GetProperty("applications")[1].GetProperty("version").GetString());
        Assert.Equal("Unknown", root.GetProperty("compatibility").GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("compatibility").GetProperty("fieldCount").GetInt32());
    }

    [Theory]
    [InlineData("Matched")]
    [InlineData("NeedsAdapter")]
    [InlineData("Unstable")]
    [InlineData("Unavailable")]
    public void KnownCompatibilityStatusAndFingerprintArePreserved(string status)
    {
        var fingerprint = new string('a', 64);
        var package = DiagnosticPackage.Create(new DiagnosticOutbox(directory), compatibility:
            new(DateTimeOffset.UtcNow, "12.9.2", fingerprint, status, 500, 3));
        using var summary = JsonDocument.Parse(Read(package)["summary.json"]);
        var compatibility = summary.RootElement.GetProperty("compatibility");
        Assert.Equal(status, compatibility.GetProperty("status").GetString());
        Assert.Equal(fingerprint, compatibility.GetProperty("structureFingerprint").GetString());
        Assert.Equal(3, compatibility.GetProperty("differenceCount").GetInt32());
    }

    private static Dictionary<string, string> Read(DiagnosticPackageContent package)
    {
        using var stream = new MemoryStream(package.Bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        });
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
