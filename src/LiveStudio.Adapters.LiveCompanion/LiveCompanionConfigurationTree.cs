using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionConfigurationTree
{
    public static IReadOnlyList<string> KnownRootSections { get; } =
    [
        "基础设置",
        "美颜设置",
        "美妆设置",
        "滤镜设置",
        "特效道具",
        "绿幕抠图",
        "镜头特效",
        "导入导出"
    ];

    public static ConfigurationTreeSnapshot Create(
        IReadOnlyList<NativeConfigurationDocument> documents,
        VerifiedAdapterDefinition? adapter)
    {
        var fields = adapter is null
            ? CreateDiscoveryFields(documents)
            : CreateMappedFields(documents, adapter);
        var sections = new List<ConfigurationSectionSnapshot>();
        for (var index = 0; index < KnownRootSections.Count; index++)
        {
            var name = KnownRootSections[index];
            var matching = fields.Where(field => FirstSegment(field.UiPath) == name).ToArray();
            sections.Add(BuildSection(name, name, index, matching, 1));
        }

        var unmapped = fields.Where(field => !KnownRootSections.Contains(FirstSegment(field.UiPath), StringComparer.Ordinal))
            .ToArray();
        if (unmapped.Length > 0)
        {
            sections.Add(BuildSection(
                "native-unmapped",
                "未映射原生字段",
                KnownRootSections.Count,
                unmapped,
                1));
        }

        var unknownCount = fields.Count(field => field.EvidenceStatus == FieldEvidenceStatus.Unknown);
        var evidenceOnlyCount = fields.Count(field => field.EvidenceStatus == FieldEvidenceStatus.EvidenceOnly);
        var mappedCount = fields.Count(field => field.EvidenceStatus == FieldEvidenceStatus.Mapped);
        var verifiedCount = fields.Count(field => field.EvidenceStatus == FieldEvidenceStatus.Verified);
        var hasCompleteUiInventory = adapter is not null
                                     && fields.Length > 0
                                     && fields.All(field => field.EvidenceStatus >= FieldEvidenceStatus.Mapped)
                                     && fields.All(field => KnownRootSections.Contains(FirstSegment(field.UiPath), StringComparer.Ordinal));
        var expectedKeys = adapter?.Definition.Fields
            .Where(LiveCompanionConfigurationStore.IsRestorableField)
            .Select(field => $"{field.StoreId}\0{field.NativePath}")
            .ToHashSet(StringComparer.Ordinal);
        var declaredKeys = adapter?.Definition.Fields
            .Select(field => $"{field.StoreId}\0{field.NativePath}")
            .ToHashSet(StringComparer.Ordinal);
        var capturedKeys = documents.SelectMany(document => document.Values.Select(value =>
                $"{document.StoreId}\0{value.JsonPointer}"))
            .ToHashSet(StringComparer.Ordinal);
        var hasCompleteNativeInventory = expectedKeys is not null
                                         && declaredKeys is not null
                                         && expectedKeys.IsSubsetOf(capturedKeys)
                                         && capturedKeys.IsSubsetOf(declaredKeys);
        return new ConfigurationTreeSnapshot(
            sections,
            unknownCount,
            evidenceOnlyCount,
            mappedCount,
            verifiedCount,
            hasCompleteUiInventory,
            hasCompleteNativeInventory);
    }

    private static ConfigurationFieldSnapshot[] CreateDiscoveryFields(
        IReadOnlyList<NativeConfigurationDocument> documents)
    {
        var fields = new List<ConfigurationFieldSnapshot>();
        foreach (var document in documents)
        {
            foreach (var value in document.Values)
            {
                var presentation = LiveCompanionDiscoveryPresentation.Present(document, value);
                ExpandField(
                    fields,
                    document,
                    value.JsonPointer,
                    value.Value,
                    presentation.UiPath,
                    FieldEvidenceStatus.EvidenceOnly,
                    writable: false,
                    controlKind: "NativeValue",
                    defaultValue: null,
                    minimum: null,
                    maximum: null,
                    step: null,
                    options: [],
                    internalId: null);
            }
        }

        return fields.ToArray();
    }

    private static ConfigurationFieldSnapshot[] CreateMappedFields(
        IReadOnlyList<NativeConfigurationDocument> documents,
        VerifiedAdapterDefinition adapter)
    {
        var byStore = documents.ToDictionary(document => document.StoreId, StringComparer.Ordinal);
        var fields = new List<ConfigurationFieldSnapshot>();
        foreach (var mapping in adapter.Definition.Fields.OrderBy(field => field.Order))
        {
            if (!byStore.TryGetValue(mapping.StoreId, out var document))
            {
                continue;
            }

            var nativeValue = document.Values.FirstOrDefault(value => value.JsonPointer == mapping.NativePath);
            if (nativeValue is null)
            {
                continue;
            }

            JsonElement? defaultValue = string.IsNullOrWhiteSpace(mapping.DefaultValueJson)
                ? null
                : JsonSerializer.Deserialize<JsonElement>(mapping.DefaultValueJson);
            var nativeName = string.IsNullOrWhiteSpace(mapping.NativeName)
                ? Leaf(mapping.NativePath)
                : mapping.NativeName;
            var uiPath = string.IsNullOrWhiteSpace(mapping.UiPath)
                ? $"未映射原生字段/{document.RelativePath}/{nativeName}"
                : mapping.UiPath;
            var evidenceStatus = string.IsNullOrWhiteSpace(mapping.UiPath)
                ? FieldEvidenceStatus.EvidenceOnly
                : mapping.EvidenceStatus;
            ExpandField(
                fields,
                document,
                mapping.NativePath,
                nativeValue.Value,
                uiPath,
                evidenceStatus,
                LiveCompanionConfigurationStore.IsRestorableField(mapping),
                mapping.ControlKind,
                defaultValue,
                mapping.Minimum,
                mapping.Maximum,
                mapping.Step,
                mapping.Options ?? [],
                mapping.InternalIdPath);
        }

        return fields.ToArray();
    }

    private static void ExpandField(
        ICollection<ConfigurationFieldSnapshot> destination,
        NativeConfigurationDocument document,
        string nativePath,
        JsonElement value,
        string uiPath,
        FieldEvidenceStatus evidenceStatus,
        bool writable,
        string controlKind,
        JsonElement? defaultValue,
        string? minimum,
        string? maximum,
        string? step,
        IReadOnlyList<string> options,
        string? internalId)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    ExpandField(
                        destination,
                        document,
                        $"{nativePath}/{EscapePointer(property.Name)}",
                        property.Value,
                        $"{uiPath}/{property.Name}",
                        evidenceStatus,
                        writable,
                        controlKind,
                        null,
                        minimum,
                        maximum,
                        step,
                        options,
                        internalId);
                }

                return;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    ExpandField(
                        destination,
                        document,
                        $"{nativePath}/{index}",
                        items[index],
                        $"{uiPath}/{index}",
                        evidenceStatus,
                        writable,
                        controlKind,
                        null,
                        minimum,
                        maximum,
                        step,
                        options,
                        internalId);
                }

                return;
            }
        }

        var name = Leaf(uiPath);
        var stableId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{document.StoreId}\0{nativePath}")))[..24];
        destination.Add(new ConfigurationFieldSnapshot(
            $"live-companion:{stableId}",
            name,
            uiPath,
            destination.Count,
            value.ValueKind.ToString(),
            controlKind,
            value.Clone(),
            defaultValue?.Clone(),
            minimum,
            maximum,
            step,
            options,
            internalId,
            new NativeLocatorSnapshot(
                document.StorageKind,
                document.StoreId,
                nativePath,
                document.TransactionBoundary,
                value.ValueKind.ToString()),
            evidenceStatus,
            writable && evidenceStatus >= FieldEvidenceStatus.Mapped,
            []));
    }

    private static ConfigurationSectionSnapshot BuildSection(
        string id,
        string name,
        int order,
        IReadOnlyList<ConfigurationFieldSnapshot> fields,
        int depth)
    {
        var directFields = fields.Where(field => Segments(field.UiPath).Length <= depth + 1).ToArray();
        var children = fields.Where(field => Segments(field.UiPath).Length > depth + 1)
            .GroupBy(field => Segments(field.UiPath)[depth], StringComparer.Ordinal)
            .Select((group, index) => BuildSection(
                $"{id}/{group.Key}",
                group.Key,
                index,
                group.ToArray(),
                depth + 1))
            .ToArray();
        return new ConfigurationSectionSnapshot(id, name, name, order, children, directFields);
    }

    private static string FirstSegment(string path) => Segments(path).FirstOrDefault() ?? string.Empty;

    private static string Leaf(string path) => Segments(path).LastOrDefault() ?? path;

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
