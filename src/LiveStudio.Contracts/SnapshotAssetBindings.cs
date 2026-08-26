namespace LiveStudio.Contracts;

public static class SnapshotAssetBindings
{
    public static IReadOnlyList<AssetBinding> Collect(IEnumerable<ApplicationSnapshot> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var bindings = applications.SelectMany(application =>
                application.Sources
                    .SelectMany(source => source.Filters)
                    .SelectMany(filter => filter.Assets)
                    .Concat((application.FilterChains ?? [])
                        .SelectMany(chain => chain.Filters)
                        .SelectMany(filter => filter.Assets)))
            .GroupBy(binding => binding.Id)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(binding => binding != first))
                {
                    throw new InvalidDataException($"素材 Binding {group.Key} 在原生来源与滤镜链中的定义不一致");
                }

                return first;
            })
            .OrderBy(binding => binding.Id)
            .ToArray();
        return bindings;
    }
}
