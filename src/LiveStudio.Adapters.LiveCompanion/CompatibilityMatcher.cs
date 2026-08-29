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
        return MatchCandidates(applicationVersion, candidates, "结构指纹");
    }

    internal static AdapterMatchResult MatchCandidates(
        string applicationVersion,
        IReadOnlyList<VerifiedAdapterDefinition> candidates,
        string matchKind)
    {
        if (!Version.TryParse(applicationVersion, out var version))
        {
            var structuralMatch = candidates.Count > 0 ? candidates[0] : null;
            return structuralMatch is null
                ? new AdapterMatchResult(AdapterMatchLevel.Incompatible, null, $"无法解析版本号且没有{matchKind}匹配")
                : new AdapterMatchResult(
                    AdapterMatchLevel.Experimental,
                    structuralMatch,
                    $"{matchKind}匹配，但目标版本号无法验证");
        }

        var verified = candidates.FirstOrDefault(adapter =>
            Version.Parse(adapter.Definition.MinimumVersion) <= version
            && Version.Parse(adapter.Definition.MaximumVersion) >= version);
        if (verified is not null)
        {
            return new AdapterMatchResult(AdapterMatchLevel.Verified, verified, $"版本和{matchKind}均已验证");
        }

        var experimental = candidates.Count > 0 ? candidates[0] : null;
        return experimental is null
            ? new AdapterMatchResult(AdapterMatchLevel.Incompatible, null, $"没有{matchKind}匹配的适配定义")
            : new AdapterMatchResult(AdapterMatchLevel.Experimental, experimental, $"{matchKind}匹配，但应用版本未验证");
    }

    internal static AdapterMatchResult MatchStructurallyCompatibleCandidates(
        IReadOnlyList<VerifiedAdapterDefinition> candidates,
        string matchKind)
    {
        if (candidates.Count == 0)
        {
            return new AdapterMatchResult(
                AdapterMatchLevel.Incompatible,
                null,
                $"没有{matchKind}匹配的签名适配定义");
        }

        var selected = candidates
            .OrderByDescending(candidate => GetDefinitionRevision(candidate.Definition.Id))
            .ThenByDescending(candidate => candidate.Definition.Id, StringComparer.Ordinal)
            .ThenByDescending(candidate => Version.Parse(candidate.Definition.MaximumVersion))
            .First();
        return new AdapterMatchResult(
            AdapterMatchLevel.Verified,
            selected,
            $"版本号已变化，但{matchKind}全部一致");
    }

    internal static AdapterMatchResult MatchPortableCapabilityCandidates(
        string applicationVersion,
        IReadOnlyList<VerifiedAdapterDefinition> candidates)
    {
        if (candidates.Count == 0)
        {
            return new AdapterMatchResult(
                AdapterMatchLevel.Incompatible,
                null,
                "没有同时包含四份原生存储和可移植摄像头结构的签名适配定义");
        }

        var selected = candidates
            .OrderByDescending(candidate => GetDefinitionRevision(candidate.Definition.Id))
            .ThenByDescending(candidate => candidate.Definition.Id, StringComparer.Ordinal)
            .ThenByDescending(candidate => Version.Parse(candidate.Definition.MaximumVersion))
            .First();
        var versionText = string.IsNullOrWhiteSpace(applicationVersion)
            ? "未知版本"
            : applicationVersion;
        return new AdapterMatchResult(
            AdapterMatchLevel.Verified,
            selected,
            $"直播伴侣 {versionText} 已按四份原生存储和可移植摄像头结构匹配；版本号不阻断保存或事务恢复");
    }

    private static int GetDefinitionRevision(string definitionId)
    {
        var marker = definitionId.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        return marker >= 0
               && int.TryParse(definitionId[(marker + 2)..], out var revision)
            ? revision
            : 0;
    }
}
