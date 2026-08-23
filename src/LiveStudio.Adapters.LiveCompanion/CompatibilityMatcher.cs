namespace LiveStudio.Adapters.LiveCompanion;

public enum AdapterMatchLevel
{
    Verified,
    Experimental,
    Incompatible
}

public sealed record AdapterMatchResult(
    AdapterMatchLevel Level,
    VerifiedAdapterDefinition? Adapter,
    string Reason);

public static class CompatibilityMatcher
{
    public static AdapterMatchResult Match(
        string applicationVersion,
        string structureFingerprint,
        IEnumerable<VerifiedAdapterDefinition> adapters)
    {
        var candidates = adapters
            .Where(adapter => string.Equals(
                adapter.Definition.StructureFingerprint,
                structureFingerprint,
                StringComparison.Ordinal))
            .ToArray();
        if (!Version.TryParse(applicationVersion, out var version))
        {
            var structuralMatch = candidates.FirstOrDefault();
            return structuralMatch is null
                ? new AdapterMatchResult(AdapterMatchLevel.Incompatible, null, "无法解析版本号且没有结构指纹匹配")
                : new AdapterMatchResult(
                    AdapterMatchLevel.Experimental,
                    structuralMatch,
                    "结构指纹匹配，但目标版本号无法验证");
        }

        var verified = candidates.FirstOrDefault(adapter =>
            Version.Parse(adapter.Definition.MinimumVersion) <= version
            && Version.Parse(adapter.Definition.MaximumVersion) >= version);
        if (verified is not null)
        {
            return new AdapterMatchResult(AdapterMatchLevel.Verified, verified, "版本和结构指纹均已验证");
        }

        var experimental = candidates.FirstOrDefault();
        return experimental is null
            ? new AdapterMatchResult(AdapterMatchLevel.Incompatible, null, "没有结构指纹匹配的适配定义")
            : new AdapterMatchResult(AdapterMatchLevel.Experimental, experimental, "结构指纹匹配，但应用版本未验证");
    }
}
