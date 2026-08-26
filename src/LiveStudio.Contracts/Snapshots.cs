using System.Text.Json;

namespace LiveStudio.Contracts;

public enum ApplicationKind
{
    Obs,
    LiveCompanion
}

public enum FieldEvidenceStatus
{
    Unknown,
    EvidenceOnly,
    Mapped,
    Verified
}

public sealed record NativeLocatorSnapshot(
    string StorageKind,
    string StoreId,
    string NativePath,
    string? Container,
    string? ValueKind);

public sealed record ConfigurationFieldSnapshot(
    string Id,
    string NativeName,
    string UiPath,
    int Order,
    string NativeType,
    string ControlKind,
    JsonElement CurrentValue,
    JsonElement? DefaultValue,
    string? Minimum,
    string? Maximum,
    string? Step,
    IReadOnlyList<string> Options,
    string? InternalId,
    NativeLocatorSnapshot Locator,
    FieldEvidenceStatus EvidenceStatus,
    bool Writable,
    IReadOnlyList<AssetBinding> Assets);

public sealed record ConfigurationSectionSnapshot(
    string Id,
    string NativeName,
    string UiPath,
    int Order,
    IReadOnlyList<ConfigurationSectionSnapshot> Sections,
    IReadOnlyList<ConfigurationFieldSnapshot> Fields);

public sealed record ConfigurationTreeSnapshot(
    IReadOnlyList<ConfigurationSectionSnapshot> Sections,
    int UnknownCount,
    int EvidenceOnlyCount,
    int MappedCount,
    int VerifiedCount,
    bool HasCompleteUiInventory,
    bool HasCompleteNativeInventory);

public sealed record FilterInstanceSnapshot(
    Guid LogicalId,
    string NativeName,
    string Kind,
    string? InternalId,
    bool Enabled,
    int Order,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<AssetBinding> Assets,
    FieldEvidenceStatus EvidenceStatus);

public sealed record FilterChainSnapshot(
    Guid SourceLogicalId,
    string NativeName,
    string UiPath,
    bool? Enabled,
    IReadOnlyList<FilterInstanceSnapshot> Filters);

public sealed record CaptureConsistency(
    string Strategy,
    string StartSha256,
    string EndSha256,
    int Attempts,
    bool IsConsistent);

public sealed record CameraCreativeLookSnapshot(
    int Contrast,
    int Highlights,
    int Shadows,
    int Fade,
    int Saturation,
    int Sharpness,
    int SharpnessRange,
    int Clarity);

public sealed record CameraStationSnapshot(
    int Slot,
    string Name,
    string Aperture,
    string ShutterSpeed,
    string Iso,
    string CreativeLook,
    CameraCreativeLookSnapshot CreativeLookSettings);

public sealed record CombinedSnapshot(
    Guid Id,
    Guid OrganizationId,
    Guid RoomId,
    string Name,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    IReadOnlyList<ApplicationSnapshot> Applications,
    IReadOnlyList<AssetBlob> Assets,
    IReadOnlyList<PreviewReference> Previews,
    IReadOnlyList<CameraStationSnapshot>? CameraStations = null);

public sealed record ApplicationSnapshot(
    ApplicationKind Kind,
    string Version,
    string AdapterId,
    string AdapterDefinitionSha256,
    string StructureFingerprint,
    CompatibilityLevel Compatibility,
    bool WasRunning,
    IReadOnlyList<CapturedParameterField> FieldCoverage,
    IReadOnlyList<VideoSource> Sources,
    IReadOnlyList<NativeConfigurationDocument> NativeDocuments,
    ConfigurationTreeSnapshot? ConfigurationTree = null,
    IReadOnlyList<FilterChainSnapshot>? FilterChains = null,
    CaptureConsistency? CaptureConsistency = null);

public sealed record NativeConfigurationDocument(
    string StoreId,
    string StorageKind,
    string StructureVersion,
    string TransactionBoundary,
    string RelativePath,
    string Sha256,
    Guid SourceLogicalId,
    IReadOnlyList<NativeConfigurationValue> Values);

public sealed record CapturedParameterField(
    string NativePath,
    string Category,
    string ValueType,
    bool Required,
    bool Writable,
    string Verification,
    string FieldId = "",
    string NativeName = "",
    string UiPath = "",
    FieldEvidenceStatus EvidenceStatus = FieldEvidenceStatus.Unknown);

public sealed record NativeConfigurationValue(
    string JsonPointer,
    string Category,
    JsonElement Value);

public sealed record VideoSource(
    Guid LogicalId,
    string Name,
    string Kind,
    CaptureDeviceDescriptor? Device,
    VideoMode? Mode,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<VideoFilter> Filters,
    string UnversionedKind = "",
    IReadOnlyDictionary<string, JsonElement>? DefaultSettings = null);

public sealed record CaptureDeviceDescriptor(
    string FriendlyName,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    string? InterfaceHint,
    IReadOnlyList<VideoMode> SupportedModes);

public sealed record VideoMode(
    int Width,
    int Height,
    int FramesPerSecondNumerator,
    int FramesPerSecondDenominator,
    string PixelFormat,
    string ColorSpace,
    string ColorRange);

public sealed record VideoFilter(
    Guid LogicalId,
    string Name,
    string Kind,
    bool Enabled,
    int Order,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<AssetBinding> Assets);

public sealed record AssetBlob(
    string Sha256,
    string MediaType,
    long Length,
    string PackagePath);

public sealed record AssetBinding(
    Guid Id,
    string BlobSha256,
    string OriginalFileName,
    string SourcePath,
    string ReferencePath,
    long Length = 0);

public sealed record PreviewReference(
    ApplicationKind Application,
    string MediaType,
    string PackagePath,
    DateTimeOffset CapturedAt);

public sealed record DeviceMapping(
    Guid Id,
    Guid OrganizationId,
    Guid DeviceId,
    Guid SourceLogicalId,
    ApplicationKind Application,
    string TargetDeviceId,
    string TargetSourceName,
    string TargetSceneName,
    bool CreateSourceWhenMissing);

public sealed record CurrentParameterState(
    Guid DeviceId,
    Guid RoomId,
    DateTimeOffset CapturedAt,
    string ParameterHash,
    IReadOnlyList<ApplicationSnapshot> Applications);
