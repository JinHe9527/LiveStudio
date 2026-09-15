using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Diagnostics;

public sealed record DiagnosticCompatibilitySummary(DateTimeOffset CheckedAt, string ApplicationVersion,
    string StructureFingerprint, string Status, int FieldCount, int DifferenceCount);

public sealed record DiagnosticPackageSummary(int SchemaVersion, Guid PackageId, DateTimeOffset ExportedAt,
    string LiveStudioVersion, string WindowsVersion, string Architecture, int ErrorKinds, int SkippedLogFiles,
    DiagnosticApplicationState[] Applications, DiagnosticCompatibilitySummary? Compatibility);

public sealed record DiagnosticPackageContent(string FileName, byte[] Bytes, int ErrorKinds, int SkippedFiles);

/// <summary>Creates a bounded, shareable package from validated projections, never by zipping a user's directory.</summary>
public static class DiagnosticPackage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static DiagnosticPackageContent Create(DiagnosticOutbox outbox,
        IEnumerable<DiagnosticApplicationState>? applications = null, DiagnosticCompatibilitySummary? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        var snapshot = outbox.ReadSnapshot();
        var now = DateTimeOffset.UtcNow;
        var summary = new DiagnosticPackageSummary(1, Guid.NewGuid(), now,
            typeof(DiagnosticPackage).Assembly.GetName().Version?.ToString() ?? "0.0",
            Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            snapshot.Reports.Length, snapshot.SkippedFiles,
            (applications ?? []).Where(state => state.Application is "Obs" or "LiveCompanion").Take(2)
                .Select(state => state with { Version = SafeVersion(state.Version) }).ToArray(),
            compatibility is null ? null : compatibility with
            {
                ApplicationVersion = SafeVersion(compatibility.ApplicationVersion),
                StructureFingerprint = compatibility.StructureFingerprint is { Length: 64 } fingerprint
                    && fingerprint.All(char.IsAsciiHexDigit) ? fingerprint.ToLowerInvariant() : "",
                Status = compatibility.Status is "Matched" or "NeedsAdapter" or "Unstable" or "Unavailable"
                    ? compatibility.Status : "Unknown",
                FieldCount = Math.Max(0, compatibility.FieldCount),
                DifferenceCount = Math.Max(0, compatibility.DifferenceCount)
            });
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "summary.json", JsonSerializer.Serialize(summary, JsonOptions));
            Write(archive, "errors.json", JsonSerializer.Serialize(snapshot.Reports, JsonOptions));
            var explanation = new StringBuilder()
                .AppendLine("LiveStudio 故障诊断包")
                .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"包编号：{summary.PackageId}")
                .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"LiveStudio：{summary.LiveStudioVersion}；Windows：{summary.WindowsVersion}；架构：{summary.Architecture}")
                .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"已记录 {summary.ErrorKinds} 类错误；跳过 {summary.SkippedLogFiles} 份损坏、不可读取或超出数量限制的记录。")
                .AppendLine("没有错误记录不代表软件已通过测试；异常发生前未启动记录、系统强制终止等情况可能没有记录。")
                .AppendLine("版本列表与兼容摘要来自最近一次检测，未检测到的信息可能为空。")
                .AppendLine("请通过 QQ 或微信将整个 ZIP 发给开发者，并另行说明：做了什么操作、预期结果、实际结果，以及大致发生时间。")
                .AppendLine("summary.json：环境和脱敏兼容摘要。errors.json：错误类型、原因码、代码位置和时间次数。")
                .AppendLine("不包含原始配置、原始异常文本、截图、登录信息、密码、用户路径或原始安装日志。")
                .AppendLine("诊断内容是排查线索，不能证明已修复或已完成真机恢复验收。")
                .AppendLine();
            foreach (var report in snapshot.Reports.Take(20))
                explanation.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{report.LastSeen:u} | {report.Component}/{report.Operation} | {report.Exceptions[0].Reason} | {report.Occurrences} 次");
            Write(archive, "说明.txt", explanation.ToString());
        }
        return new($"LiveStudio-诊断-{now:yyyyMMdd-HHmmss}-{summary.PackageId.ToString("N")[..8]}.zip",
            output.ToArray(), summary.ErrorKinds, summary.SkippedLogFiles);
    }

    private static string SafeVersion(string value) => Version.TryParse(value, out var version) ? version.ToString() : "0.0";

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
