using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

public enum ConfigurationStorageKind
{
    JsonFile,
    BinaryFile,
    Registry,
    Sqlite,
    LevelDb,
    NativeExport,
    Ipc
}

public enum UnifiedFieldKind
{
    DeviceSelection,
    Width,
    Height,
    FramesPerSecond,
    PixelFormat,
    ColorSpace,
    ColorRange,
    FilterType,
    FilterEnabled,
    FilterOrder,
    FilterSetting,
    FilterAsset,
    NativeField
}

public sealed record UiSectionDefinition(
    string Id,
    string NativeName,
    string UiPath,
    int Order,
    string? ParentId = null);

public sealed record ConfigurationStoreDefinition(
    string Id,
    ConfigurationStorageKind Kind,
    string Location,
    string? Container,
    bool RequiresApplicationStop);

public sealed record FieldMappingDefinition(
    string Id,
    UnifiedFieldKind UnifiedKind,
    string StoreId,
    string NativePath,
    string ValueType,
    bool Required,
    bool Writable,
    string NativeName = "",
    string UiPath = "",
    int Order = 0,
    string ControlKind = "Unknown",
    string? DefaultValueJson = null,
    string? Minimum = null,
    string? Maximum = null,
    string? Step = null,
    IReadOnlyList<string>? Options = null,
    string? InternalIdPath = null,
    FieldEvidenceStatus EvidenceStatus = FieldEvidenceStatus.Mapped);

public sealed record LiveStateRuleDefinition(
    string StoreId,
    string NativePath,
    string ExpectedIdleValue);

public sealed record ScreenshotRuleDefinition(string Method, string Target);

public sealed record LiveCompanionAdapterDefinition(
    string Id,
    string MinimumVersion,
    string MaximumVersion,
    string StructureFingerprint,
    IReadOnlyList<ConfigurationStoreDefinition> Stores,
    IReadOnlyList<FieldMappingDefinition> Fields,
    IReadOnlyList<string> ExcludedNativePaths,
    LiveStateRuleDefinition LiveStateRule,
    ScreenshotRuleDefinition ScreenshotRule,
    IReadOnlyList<UiSectionDefinition>? UiSections = null,
    string OnlineCaptureStrategy = "DoubleReadHash");

public sealed record AdapterDefinitionSignature(
    string Algorithm,
    string KeyId,
    string DefinitionSha256,
    string SignatureBase64);

public sealed record VerifiedAdapterDefinition(
    LiveCompanionAdapterDefinition Definition,
    string KeyId,
    string DefinitionSha256);
