using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveStudio.Packaging;

public static partial class SensitiveDataScanner
{
    private const int MaximumEmbeddedJsonDepth = 4;

    public static IReadOnlyList<string> ScanJson(ReadOnlySpan<byte> content, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            var findings = new List<string>();
            ScanElement(document.RootElement, path, findings, 0);
            return findings;
        }
        catch (JsonException)
        {
            return [$"{path}: JSON 无法解析"];
        }
    }

    public static IReadOnlyList<string> ScanText(ReadOnlySpan<byte> content, string path)
    {
        var text = Encoding.UTF8.GetString(content);
        var findings = new List<string>();
        foreach (Match match in SensitiveAssignmentPattern().Matches(text))
        {
            findings.Add($"{path}: 检测到敏感字段 {match.Groups[1].Value}");
        }

        if (BearerPattern().IsMatch(text))
        {
            findings.Add($"{path}: 检测到 Bearer 凭据");
        }

        return findings;
    }

    private static void ScanElement(
        JsonElement element,
        string path,
        List<string> findings,
        int embeddedJsonDepth)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (SensitiveKeyPattern().IsMatch(property.Name))
                {
                    findings.Add($"{propertyPath}: 禁止归档敏感字段");
                    continue;
                }

                ScanElement(property.Value, propertyPath, findings, embeddedJsonDepth);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in element.EnumerateArray())
            {
                ScanElement(value, $"{path}[{index}]", findings, embeddedJsonDepth);
                index++;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? string.Empty;
            if (BearerPattern().IsMatch(value))
            {
                findings.Add($"{path}: 检测到 Bearer 凭据");
            }

            ScanEmbeddedJson(value, path, findings, embeddedJsonDepth);
        }
    }

    private static void ScanEmbeddedJson(
        string value,
        string path,
        List<string> findings,
        int embeddedJsonDepth)
    {
        if (embeddedJsonDepth >= MaximumEmbeddedJsonDepth)
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 2
            || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return;
        }

        try
        {
            using var embedded = JsonDocument.Parse(trimmed);
            ScanElement(
                embedded.RootElement,
                $"{path}（内嵌 JSON）",
                findings,
                embeddedJsonDepth + 1);
        }
        catch (JsonException)
        {
            // 普通文本可能以大括号开头；只有可解析的内嵌 JSON 才参与敏感字段审计。
        }
    }

    [GeneratedRegex("password|passwd|cookie|token|stream[_ -]?key|secret|credential|authorization", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyPattern();

    [GeneratedRegex("(?im)^\\s*(password|passwd|cookie|token|stream[_ -]?key|secret|credential|authorization)\\s*[:=]")]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex("\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();
}
