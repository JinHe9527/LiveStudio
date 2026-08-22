namespace LiveStudio.Adapters.LiveCompanion;

public enum ConfigurationStorageKind
{
    JsonFile,
    Registry,
    Sqlite,
    LevelDb
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
    FilterAsset
}

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
    bool Writable);

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
    ScreenshotRuleDefinition ScreenshotRule);

public sealed record AdapterDefinitionSignature(
    string Algorithm,
    string KeyId,
    string DefinitionSha256,
    string SignatureBase64);

public sealed record VerifiedAdapterDefinition(
    LiveCompanionAdapterDefinition Definition,
    string KeyId,
    string DefinitionSha256);
