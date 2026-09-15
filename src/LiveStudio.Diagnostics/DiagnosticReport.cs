using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LiveStudio.Diagnostics;

public sealed record DiagnosticFailure(string Type, int HResult, string Reason, string[] Frames);
public sealed record DiagnosticApplicationState(string Application, string Version, bool IsRunning, bool AdapterAvailable);

public sealed record DiagnosticReport(
    int SchemaVersion, Guid ReportId, string Fingerprint, string Component, string Operation,
    string Version, string OperatingSystem, string Architecture, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, int Occurrences, DiagnosticFailure[] Exceptions,
    DiagnosticApplicationState[]? Applications = null);

/// <summary>Only code-owned symbols and fixed classifications leave the process. Never serializes exception messages or Data.</summary>
public static class DiagnosticReportFactory
{
    public static DiagnosticReport Create(string component, string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var failures = new List<DiagnosticFailure>();
        for (var current = exception; current is not null && failures.Count < 5; current = current.InnerException)
        {
            var type = current.GetType();
            var typeName = IsTrusted(type.Assembly) ? type.Name : "ExternalException";
            var frames = new StackTrace(current, false).GetFrames()
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType is { } declaring && IsTrusted(declaring.Assembly))
                .Take(16)
                .Select(method => $"{method!.DeclaringType!.Namespace}.{method.DeclaringType.Name}.{method.Name}")
                .Select(frame => frame.Length <= 160 ? frame : frame[..160])
                .ToArray();
            failures.Add(new(typeName, current.HResult, Classify(current), frames));
        }
        var version = typeof(DiagnosticReportFactory).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        component = CodeSymbol(component);
        operation = CodeSymbol(operation);
        var fingerprintInput = string.Join('|', new[] { component, operation, version }
            .Concat(failures.Select(item => $"{item.Type}:{item.HResult}:{item.Reason}:{string.Join(';', item.Frames)}")));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
        var now = DateTimeOffset.UtcNow;
        return new(1, Guid.NewGuid(), fingerprint, component, operation, version,
            Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            now, now, 1, failures.ToArray());
    }

    private static bool IsTrusted(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        return name.StartsWith("LiveStudio.", StringComparison.Ordinal)
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name.StartsWith("Avalonia", StringComparison.Ordinal);
    }

    private static string CodeSymbol(string value) => value.Length is > 0 and <= 100
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_')
        ? value : "Unknown";

    private static string Classify(Exception exception)
    {
        if (exception is DiagnosticOperationException operation) return "Operation." + operation.Code;
        // These phrases identify existing product failures; their original messages can contain private paths/values.
        var message = exception.Message;
        if (message.Contains("配置目录候选过多", StringComparison.Ordinal)) return "CompanionDirectoryLimit";
        if (message.Contains("发现多个直播伴侣配置目录", StringComparison.Ordinal)) return "CompanionMultipleRoots";
        if (message.Contains("WBStore 是目录链接", StringComparison.Ordinal)) return "CompanionDirectoryLink";
        if (message.Contains("无读取权限", StringComparison.Ordinal) || message.Contains("无权检查", StringComparison.Ordinal)) return "AccessDenied";
        if (message.Contains("JSON 无效或正在写入", StringComparison.Ordinal)) return "InvalidJson";
        if (message.Contains("文件被独占", StringComparison.Ordinal)) return "CompanionFileLocked";
        if (message.Contains("文件或目录不存在", StringComparison.Ordinal)) return "ResourceNotFound";
        if (message.Contains("没有可读取的目标字段", StringComparison.Ordinal)) return "CompanionTargetFieldsMissing";
        if (message.Contains("配置在读取过程中持续变化", StringComparison.Ordinal)) return "CompanionConfigurationChanging";
        if (message.Contains("data2", StringComparison.OrdinalIgnoreCase)) return "CompanionSecondaryContainer";
        if (message.Contains("未找到", StringComparison.Ordinal) || message.Contains("未在", StringComparison.Ordinal)) return "ConfigurationOrResourceNotFound";
        if (message.Contains("签名", StringComparison.Ordinal)) return "SignatureValidationFailed";
        if (message.Contains("回滚", StringComparison.Ordinal)) return "RollbackFailure";
        if (message.Contains("回读", StringComparison.Ordinal)) return "ReadbackMismatch";
        if (message.Contains("适配", StringComparison.Ordinal)) return "AdapterCompatibilityFailure";
        if (message.Contains("无法生成可跨电脑恢复", StringComparison.Ordinal)) return "CompanionPortableProfileUnsupported";
        return exception switch
        {
            UnauthorizedAccessException => "AccessDenied",
            FileNotFoundException or DirectoryNotFoundException => "ResourceNotFound",
            TimeoutException or OperationCanceledException => "Timeout",
            HttpRequestException => "NetworkFailure",
            System.Text.Json.JsonException => "InvalidJson",
            System.Security.Cryptography.CryptographicException => "CryptographicFailure",
            IOException => "StorageOrPipeFailure",
            _ => "UnhandledOperationFailure"
        };
    }
}
