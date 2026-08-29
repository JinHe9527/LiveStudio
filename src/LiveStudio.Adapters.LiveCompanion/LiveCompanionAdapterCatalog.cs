using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapterCatalog
{
    private readonly string catalogDirectory;
    private readonly Lazy<IReadOnlyList<VerifiedAdapterDefinition>> adapters;

    public LiveCompanionAdapterCatalog()
        : this(ResolveDefaultDirectory())
    {
    }

    public LiveCompanionAdapterCatalog(string catalogDirectory)
    {
        this.catalogDirectory = Path.GetFullPath(catalogDirectory);
        adapters = new Lazy<IReadOnlyList<VerifiedAdapterDefinition>>(Load, true);
    }

    public AdapterMatchResult Match(string applicationVersion, string structureFingerprint) =>
        CompatibilityMatcher.Match(applicationVersion, structureFingerprint, adapters.Value);

    public AdapterMatchResult Match(
        string applicationVersion,
        string structureFingerprint,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var exact = Match(applicationVersion, structureFingerprint);
        if (exact.Adapter is not null)
        {
            return exact;
        }

        var requiredShapeMatches = adapters.Value
            .Where(adapter => MatchesCompatibleShape(adapter.Definition, discoveredDocuments))
            .ToArray();
        return CompatibilityMatcher.MatchStructurallyCompatibleCandidates(
            requiredShapeMatches,
            "签名定义的必需字段结构");
    }

    public AdapterMatchResult MatchSnapshot(
        string applicationVersion,
        string adapterId,
        string definitionSha256,
        string structureFingerprint)
    {
        var candidates = adapters.Value.Where(adapter =>
            string.Equals(adapter.Definition.Id, adapterId, StringComparison.Ordinal)
            && string.Equals(adapter.DefinitionSha256, definitionSha256, StringComparison.Ordinal)
            && string.Equals(
                adapter.Definition.StructureFingerprint,
                structureFingerprint,
                StringComparison.Ordinal)).ToArray();
        return CompatibilityMatcher.MatchCandidates(
            applicationVersion,
            candidates,
            "存档签名定义");
    }

    public AdapterMatchResult MatchSnapshot(
        string applicationVersion,
        string adapterId,
        string definitionSha256,
        string structureFingerprint,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var versionMatch = MatchSnapshot(
            applicationVersion,
            adapterId,
            definitionSha256,
            structureFingerprint);
        if (versionMatch.Level == AdapterMatchLevel.Verified)
        {
            return versionMatch;
        }

        var structuralMatches = adapters.Value.Where(adapter =>
            string.Equals(adapter.Definition.Id, adapterId, StringComparison.Ordinal)
            && string.Equals(adapter.DefinitionSha256, definitionSha256, StringComparison.Ordinal)
            && string.Equals(
                adapter.Definition.StructureFingerprint,
                structureFingerprint,
                StringComparison.Ordinal)
            && MatchesCompatibleShape(adapter.Definition, discoveredDocuments)).ToArray();
        return CompatibilityMatcher.MatchStructurallyCompatibleCandidates(
            structuralMatches,
            "目标电脑全部可恢复字段路径和类型");
    }

    public IReadOnlyList<VerifiedAdapterDefinition> GetAll() => adapters.Value;

    internal AdapterMatchResult MatchPortableTarget(
        string applicationVersion,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var candidates = adapters.Value.Where(adapter =>
            LiveCompanionPortableProfile.CanRestoreTo(adapter.Definition, discoveredDocuments))
            .ToArray();
        return CompatibilityMatcher.MatchPortableCapabilityCandidates(
            applicationVersion,
            candidates);
    }

    internal static bool MatchesRequiredShape(
        LiveCompanionAdapterDefinition definition,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var runtimeBinding = LiveCompanionRuntimeBinding.TryCreate(
            definition,
            discoveredDocuments);
        if (runtimeBinding is null)
        {
            return false;
        }

        var documentsByLocation = discoveredDocuments
            .GroupBy(document => NormalizeLocation(document.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var stores = definition.Stores.ToDictionary(store => store.Id, StringComparer.Ordinal);
        foreach (var field in definition.Fields.Where(
                     LiveCompanionConfigurationStore.IsRequiredRestorableField))
        {
            if (!stores.TryGetValue(field.StoreId, out var store)
                || !documentsByLocation.TryGetValue(NormalizeLocation(store.Location), out var document))
            {
                return false;
            }

            var runtimePath = runtimeBinding.ToRuntimePointer(field.NativePath);
            var value = document.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.JsonPointer, runtimePath, StringComparison.Ordinal));
            if (value is null || !MatchesValueType(field.ValueType, value.Value.ValueKind))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool MatchesCompatibleShape(
        LiveCompanionAdapterDefinition definition,
        IReadOnlyList<NativeConfigurationDocument> discoveredDocuments)
    {
        var runtimeBinding = LiveCompanionRuntimeBinding.TryCreate(
            definition,
            discoveredDocuments);
        if (runtimeBinding is null || !MatchesRequiredShape(definition, discoveredDocuments))
        {
            return false;
        }

        var stores = definition.Stores.ToDictionary(store => store.Id, StringComparer.Ordinal);
        var declaredByLocation = definition.Fields
            .GroupBy(field => NormalizeLocation(stores[field.StoreId].Location), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    field => runtimeBinding.ToRuntimePointer(field.NativePath),
                    StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);
        if (discoveredDocuments
            .Select(document => NormalizeLocation(document.RelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != definition.Stores.Count)
        {
            return false;
        }

        foreach (var document in discoveredDocuments)
        {
            if (!declaredByLocation.TryGetValue(
                    NormalizeLocation(document.RelativePath),
                    out var declaredFields))
            {
                return false;
            }

            foreach (var value in document.Values)
            {
                if (!declaredFields.TryGetValue(value.JsonPointer, out var field)
                    || !MatchesValueType(field.ValueType, value.Value.ValueKind))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string NormalizeLocation(string location) => location
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        .TrimStart(Path.DirectorySeparatorChar);

    private static bool MatchesValueType(string valueType, JsonValueKind valueKind) =>
        valueType.Trim().ToLowerInvariant() switch
        {
            "string" => valueKind == JsonValueKind.String,
            "int" or "integer" or "number" or "double" => valueKind == JsonValueKind.Number,
            "bool" or "boolean" => valueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => valueKind == JsonValueKind.Object,
            "array" => valueKind == JsonValueKind.Array,
            "null" => valueKind == JsonValueKind.Null,
            _ => false
        };

    private List<VerifiedAdapterDefinition> Load()
    {
        if (!Directory.Exists(catalogDirectory))
        {
            return [];
        }

        var trustedKeyDirectory = Path.Combine(catalogDirectory, "trusted-keys");
        var result = new List<VerifiedAdapterDefinition>();
        foreach (var definitionPath in Directory.EnumerateFiles(
                     catalogDirectory,
                     "*.adapter.json",
                     SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            var signaturePath = definitionPath[..^".adapter.json".Length] + ".signature.json";
            if (!File.Exists(signaturePath))
            {
                throw new AdapterDefinitionException($"适配定义缺少签名: {Path.GetFileName(definitionPath)}");
            }

            result.Add(AdapterDefinitionVerifier.Verify(
                File.ReadAllBytes(definitionPath),
                File.ReadAllBytes(signaturePath),
                keyId => LoadTrustedKey(trustedKeyDirectory, keyId)));
        }

        return result;
    }

    private static ECDsa? LoadTrustedKey(string directory, string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)
            || keyId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || keyId.Contains(Path.DirectorySeparatorChar)
            || keyId.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var path = Path.Combine(directory, $"{keyId}.pem");
        if (!File.Exists(path))
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string ResolveDefaultDirectory()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Adapters");
        return Directory.Exists(bundled)
            ? bundled
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveStudio",
                "Adapters");
    }
}
