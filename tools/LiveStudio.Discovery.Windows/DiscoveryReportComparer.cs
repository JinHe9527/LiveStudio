namespace LiveStudio.Discovery.Windows;

public static class DiscoveryReportComparer
{
    public static DiscoveryDifference Compare(DiscoveryReport before, DiscoveryReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeFiles = before.Files.ToDictionary(FileKey, StringComparer.OrdinalIgnoreCase);
        var afterFiles = after.Files.ToDictionary(FileKey, StringComparer.OrdinalIgnoreCase);
        var beforeRegistry = before.RegistryValues.ToDictionary(RegistryKey, StringComparer.OrdinalIgnoreCase);
        var afterRegistry = after.RegistryValues.ToDictionary(RegistryKey, StringComparer.OrdinalIgnoreCase);
        return new DiscoveryDifference(
            before.Name,
            after.Name,
            DateTimeOffset.UtcNow,
            afterFiles.Keys.Except(beforeFiles.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            beforeFiles.Keys.Except(afterFiles.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            beforeFiles.Keys.Intersect(afterFiles.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(key => !string.Equals(beforeFiles[key].Sha256, afterFiles[key].Sha256, StringComparison.Ordinal))
                .Order()
                .ToArray(),
            afterRegistry.Keys.Except(beforeRegistry.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            beforeRegistry.Keys.Except(afterRegistry.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            beforeRegistry.Keys.Intersect(afterRegistry.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(key => !string.Equals(beforeRegistry[key].ValueHash, afterRegistry[key].ValueHash, StringComparison.Ordinal))
                .Order()
                .ToArray());
    }

    private static string FileKey(FileObservation observation) =>
        $"{observation.Root}|{observation.RelativePath}";

    private static string RegistryKey(RegistryObservation observation) =>
        $"{observation.KeyPath}|{observation.ValueName}";
}
