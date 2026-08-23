namespace LiveStudio.Packaging;

public static class NativeExportReportComparer
{
    public static NativeExportDifference Compare(NativeExportReport before, NativeExportReport after)
    {
        var beforeEntries = before.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var afterEntries = after.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var beforeFields = FlattenFields(before);
        var afterFields = FlattenFields(after);
        return new NativeExportDifference(
            before.Name,
            after.Name,
            DateTimeOffset.UtcNow,
            afterEntries.Keys.Except(beforeEntries.Keys, StringComparer.Ordinal).Order().ToArray(),
            beforeEntries.Keys.Except(afterEntries.Keys, StringComparer.Ordinal).Order().ToArray(),
            beforeEntries.Keys.Intersect(afterEntries.Keys, StringComparer.Ordinal)
                .Where(path => !string.Equals(beforeEntries[path].Sha256, afterEntries[path].Sha256, StringComparison.Ordinal))
                .Order()
                .ToArray(),
            afterFields.Keys.Except(beforeFields.Keys, StringComparer.Ordinal).Select(DisplayFieldKey).Order().ToArray(),
            beforeFields.Keys.Except(afterFields.Keys, StringComparer.Ordinal).Select(DisplayFieldKey).Order().ToArray(),
            beforeFields.Keys.Intersect(afterFields.Keys, StringComparer.Ordinal)
                .Where(key => !string.Equals(beforeFields[key].ValueHash, afterFields[key].ValueHash, StringComparison.Ordinal)
                    || !string.Equals(beforeFields[key].ValueType, afterFields[key].ValueType, StringComparison.Ordinal))
                .Select(DisplayFieldKey)
                .Order()
                .ToArray(),
            before.SensitivePaths.Concat(after.SensitivePaths).Distinct(StringComparer.Ordinal).Order().ToArray());
    }

    private static Dictionary<string, NativeExportFieldObservation> FlattenFields(NativeExportReport report) => report.Entries
        .SelectMany(entry => entry.Fields.Select(field => new
        {
            Key = $"{entry.Path}\0{field.JsonPointer}",
            Field = field
        }))
        .ToDictionary(value => value.Key, value => value.Field, StringComparer.Ordinal);

    private static string DisplayFieldKey(string key) => key.Replace('\0', ':');
}
