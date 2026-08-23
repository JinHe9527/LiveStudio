using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Packaging;

public static class NativeExportInspector
{
    private const int MaximumEntryCount = 10_000;
    private const long MaximumEntryLength = 128L * 1024 * 1024;
    private const long MaximumExpandedLength = 512L * 1024 * 1024;
    private static readonly string[] SensitiveTerms =
    [
        "account", "authorization", "cookie", "credential", "did", "login", "oauth", "password",
        "passport", "secret", "session", "streamkey", "token", "uid"
    ];

    public static async Task<NativeExportReport> InspectAsync(
        string name,
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("直播伴侣原生导出包不存在", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        await using var sourceHashStream = File.OpenRead(fullPath);
        var sourceSha256 = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(sourceHashStream, cancellationToken));
        await using var archiveStream = File.OpenRead(fullPath);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"原生导出包条目过多: {archive.Entries.Count}");
        }

        var entries = new List<NativeExportEntryObservation>();
        var sensitivePaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        long expandedLength = 0;
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var entryPath = NormalizeEntryPath(entry.FullName);
            if (!seenPaths.Add(entryPath))
            {
                throw new InvalidDataException($"原生导出包包含重复条目: {entryPath}");
            }

            if (entry.Length is < 0 or > MaximumEntryLength)
            {
                throw new InvalidDataException($"原生导出包条目过大: {entryPath}");
            }

            expandedLength = checked(expandedLength + entry.Length);
            if (expandedLength > MaximumExpandedLength)
            {
                throw new InvalidDataException("原生导出包解压后总大小超过限制");
            }

            await using var entryStream = entry.Open();
            using var content = new MemoryStream(checked((int)entry.Length));
            await entryStream.CopyToAsync(content, cancellationToken);
            if (content.Length != entry.Length)
            {
                throw new InvalidDataException($"原生导出包条目长度不一致: {entryPath}");
            }

            var bytes = content.ToArray();
            var format = DetectFormat(entryPath, bytes);
            var fields = new List<NativeExportFieldObservation>();
            if (ContainsSensitiveTerm(entryPath))
            {
                sensitivePaths.Add(entryPath);
            }
            else if (string.Equals(format, "JSON", StringComparison.Ordinal))
            {
                CollectJsonFields(entryPath, bytes, fields, sensitivePaths);
            }

            entries.Add(new NativeExportEntryObservation(
                entryPath,
                format,
                entry.Length,
                entry.CompressedLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                fields.OrderBy(field => field.JsonPointer, StringComparer.Ordinal).ToArray()));
        }

        return new NativeExportReport(
            name,
            DateTimeOffset.UtcNow,
            fileInfo.Name,
            fileInfo.Length,
            sourceSha256,
            entries,
            sensitivePaths.Distinct(StringComparer.Ordinal).Order().ToArray());
    }

    private static string NormalizeEntryPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized[0] == '/'
            || normalized.Contains('\0')
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"原生导出包条目路径不安全: {value}");
        }

        return normalized;
    }

    private static string DetectFormat(string path, byte[] content)
    {
        if (content.AsSpan().StartsWith("SQLite format 3\0"u8))
        {
            return "SQLite";
        }

        var firstContent = FindFirstContentByte(content);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || firstContent >= 0 && content[firstContent] is (byte)'{' or (byte)'[')
        {
            try
            {
                using var _ = JsonDocument.Parse(content.AsMemory());
                return "JSON";
            }
            catch (JsonException)
            {
                return "InvalidJSON";
            }
        }

        var fileName = Path.GetFileName(path);
        return Path.GetExtension(path).Equals(".ldb", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
                ? "LevelDB"
                : "File";
    }

    private static void CollectJsonFields(
        string entryPath,
        byte[] content,
        ICollection<NativeExportFieldObservation> fields,
        ICollection<string> sensitivePaths)
    {
        using var json = JsonDocument.Parse(content.AsMemory());
        CollectJsonValue(entryPath, json.RootElement, string.Empty, fields, sensitivePaths);
    }

    private static void CollectJsonValue(
        string entryPath,
        JsonElement value,
        string pointer,
        ICollection<NativeExportFieldObservation> fields,
        ICollection<string> sensitivePaths)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    var childPointer = $"{pointer}/{EscapePointer(property.Name)}";
                    if (ContainsSensitiveTerm(property.Name))
                    {
                        sensitivePaths.Add($"{entryPath}:{childPointer}");
                        continue;
                    }

                    CollectJsonValue(entryPath, property.Value, childPointer, fields, sensitivePaths);
                }

                return;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    CollectJsonValue(entryPath, items[index], $"{pointer}/{index}", fields, sensitivePaths);
                }

                return;
            }
        }

        var rawValue = Encoding.UTF8.GetBytes(value.GetRawText());
        fields.Add(new NativeExportFieldObservation(
            string.IsNullOrEmpty(pointer) ? "/" : pointer,
            value.ValueKind.ToString(),
            Convert.ToHexStringLower(SHA256.HashData(rawValue))));
    }

    private static bool ContainsSensitiveTerm(string value)
    {
        var normalized = string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return SensitiveTerms.Any(normalized.Contains);
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static int FindFirstContentByte(byte[] content)
    {
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return index;
            }
        }

        return -1;
    }
}
