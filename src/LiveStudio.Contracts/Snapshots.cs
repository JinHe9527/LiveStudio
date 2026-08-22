using System.Text.Json;

namespace LiveStudio.Contracts;

public enum ApplicationKind
{
    Obs,
    LiveCompanion
}

public sealed record CombinedSnapshot(
    Guid Id,
    Guid OrganizationId,
    Guid RoomId,
    string Name,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    IReadOnlyList<ApplicationSnapshot> Applications,
    IReadOnlyList<AssetReference> Assets,
    IReadOnlyList<PreviewReference> Previews);

public sealed record ApplicationSnapshot(
    ApplicationKind Kind,
    string Version,
    string ConfigurationFingerprint,
    IReadOnlyList<VideoSource> Sources,
    IReadOnlyList<NativeConfigurationDocument> NativeDocuments);

public sealed record NativeConfigurationDocument(
    string StoreId,
    string RelativePath,
    string Sha256,
    Guid SourceLogicalId,
    IReadOnlyList<NativeConfigurationValue> Values);

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
    IReadOnlyList<VideoFilter> Filters);

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
    IReadOnlyList<AssetReference> Assets);

public sealed record AssetReference(
    string Sha256,
    string OriginalFileName,
    string MediaType,
    long Length,
    string PackagePath,
    string SourcePath);

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
