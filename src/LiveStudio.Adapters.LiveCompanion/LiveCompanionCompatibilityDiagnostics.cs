using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionCompatibilityDiagnostics
{
    public static TargetCompatibilityReport Analyze(string version,
        IReadOnlyList<NativeConfigurationDocument> documents, LiveCompanionAdapterCatalog catalog)
    {
        var fingerprint = LiveCompanionStructureFingerprint.Compute(documents);
        var profile = LiveCompanionPortableProfile.TryCreate(documents, out _);
        var candidates = catalog.GetAll().Select(adapter =>
        {
            var projected = profile is null ? documents : profile.CreateExpectedDocuments(
                adapter, new Dictionary<Guid, DeviceMapping>(), [], Path.GetTempPath());
            var binding = LiveCompanionRuntimeBinding.TryCreate(adapter.Definition,
                profile is null ? documents : [profile.SourceStoreDocument, profile.EffectConfigurationDocument]);
            var stores = adapter.Definition.Stores.ToDictionary(store => store.Id, StringComparer.Ordinal);
            var expected = adapter.Definition.Fields.Select(field => new
            {
                Store = Normalize(stores[field.StoreId].Location),
                Path = binding?.ToRuntimePointer(field.NativePath) ?? field.NativePath,
                Field = field
            }).ToDictionary(item => (item.Store, item.Path));
            var actual = projected.SelectMany(document => document.Values.Select(value => new
            {
                Store = Normalize(document.RelativePath),
                Path = value.JsonPointer,
                Type = TypeName(value.Value.ValueKind)
            })).ToDictionary(item => (item.Store, item.Path));
            var differences = new List<CompatibilityFieldDifference>();
            foreach (var item in actual.Values)
            {
                if (!expected.TryGetValue((item.Store, item.Path), out var declared))
                {
                    differences.Add(new(item.Store, item.Path, "UnknownField", null, item.Type));
                }
                else if (NormalizeType(declared.Field.ValueType) != item.Type)
                {
                    differences.Add(new(item.Store, item.Path, "TypeChanged", NormalizeType(declared.Field.ValueType), item.Type));
                }
            }
            foreach (var item in expected.Values.Where(item => LiveCompanionConfigurationStore.IsRequiredRestorableField(item.Field)))
            {
                if (!actual.ContainsKey((item.Store, item.Path)))
                {
                    differences.Add(new(item.Store, item.Path, "MissingField", NormalizeType(item.Field.ValueType), null));
                }
            }
            foreach (var store in adapter.Definition.Stores)
            {
                if (!documents.Any(document => Normalize(document.RelativePath) == Normalize(store.Location)))
                {
                    differences.Add(new(Normalize(store.Location), "", "MissingStore", null, null));
                }
            }
            var matched = differences.Count == 0 && binding is not null
                && (profile is null
                    ? LiveCompanionAdapterCatalog.MatchesCompatibleShape(adapter.Definition, documents)
                    : LiveCompanionPortableProfile.ValidateTargetSelection(documents).CanProceed
                      && LiveCompanionPortableProfile.CanRestoreTo(adapter.Definition, documents)
                      && LiveCompanionAdapterCatalog.MatchesPortableRestoreVersion(version, adapter, documents));
            var versionMatched = Version.TryParse(version, out var parsed)
                && parsed >= Version.Parse(adapter.Definition.MinimumVersion)
                && parsed <= Version.Parse(adapter.Definition.MaximumVersion);
            return new { Adapter = adapter, Differences = differences, Matched = matched, VersionMatched = versionMatched, Count = actual.Count };
        }).OrderByDescending(candidate => candidate.Matched)
            .ThenBy(candidate => candidate.Differences.Count)
            .ThenByDescending(candidate => candidate.VersionMatched)
            .ThenByDescending(candidate => candidate.Adapter.Definition.Id, StringComparer.Ordinal).FirstOrDefault();
        var matched = documents.Count > 0 && candidates?.Matched == true;
        return new(DateTimeOffset.UtcNow, version, fingerprint,
            matched ? "Matched" : "NeedsAdapter", candidates?.Adapter.Definition.Id,
            matched ? "已自动匹配签名参数结构；恢复时仍需检查设备、素材并逐项回读。"
                : $"参数结构未匹配：缺少 {candidates?.Differences.Count(item => item.Kind is "MissingField" or "MissingStore") ?? 0} 项，"
                  + $"新增 {candidates?.Differences.Count(item => item.Kind == "UnknownField") ?? 0} 项，"
                  + $"类型变化 {candidates?.Differences.Count(item => item.Kind == "TypeChanged") ?? 0} 项。请导出报告检查适配或来源结构。",
            candidates?.Count ?? 0, candidates?.Differences ?? []);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    private static string NormalizeType(string type) => type.ToLowerInvariant() switch
    {
        "int" or "integer" or "double" => "number",
        "bool" => "boolean",
        var value => value
    };
    private static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => kind.ToString().ToLowerInvariant()
    };
}
