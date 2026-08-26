namespace LiveStudio.Desktop.ViewModels;

public static class RestoreMappingMatcher
{
    public static LocalMappingTargetItemViewModel? FindAutomaticTarget(
        LocalMappingSourceItemViewModel source,
        IReadOnlyList<LocalMappingTargetItemViewModel> targets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        var candidates = targets.Where(target => target.Application == source.Application).ToArray();
        var sameName = candidates.Where(target => string.Equals(
                target.SourceName,
                source.SourceName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sameName.Length == 1)
        {
            return sameName[0];
        }

        var sameDevice = candidates.Where(target => string.Equals(
                target.DeviceName,
                source.DeviceName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return sameDevice.Length == 1 ? sameDevice[0] : null;
    }
}
