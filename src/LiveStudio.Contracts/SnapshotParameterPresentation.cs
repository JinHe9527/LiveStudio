using System.Text.Json;

namespace LiveStudio.Contracts;

public sealed record PresentedSnapshotSetting(string Name, string Value, string TechnicalName);

/// <summary>
/// Formats native snapshot values without inventing application semantics. The display name is always
/// the exact native key; UI names and hierarchy are supplied only by ConfigurationTreeSnapshot evidence.
/// </summary>
public static class SnapshotParameterPresentation
{
    public static IReadOnlyList<PresentedSnapshotSetting> PresentSourceSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => PresentSettings(settings);

    public static IReadOnlyList<PresentedSnapshotSetting> PresentFilterSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => PresentSettings(settings);

    public static string PresentTechnicalName(string name) => name;

    public static string SourceKindName(string kind) => kind;

    public static string FilterKindName(string kind) => kind;

    public static string ColorSpace(string? value) => EmptyAsUnknown(value);

    public static string ColorRange(string? value) => EmptyAsUnknown(value);

    private static PresentedSnapshotSetting[] PresentSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => settings
        .OrderBy(setting => setting.Key, StringComparer.Ordinal)
        .Select(setting => new PresentedSnapshotSetting(
            setting.Key.Split('/').LastOrDefault() ?? setting.Key,
            FormatSettingValue(setting.Value),
            setting.Key))
        .ToArray();

    private static string FormatSettingValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };

    private static string EmptyAsUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "未记录" : value;
}
