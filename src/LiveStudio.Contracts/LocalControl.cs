using System.Buffers.Binary;
using System.Text.Json;

namespace LiveStudio.Contracts;

public enum LocalControlMethod
{
    GetState,
    RefreshCurrentState,
    CaptureSnapshot,
    RestoreSnapshot,
    ConfigureObs,
    AutoConfigureObs,
    ConfigureLanDirectory,
    ConfigureAutoStart,
    EnrollDevice,
    GetMappingContext,
    GetSnapshotDetail,
    GetSnapshotPreview,
    SaveDeviceMapping,
    InspectSnapshotFile,
    ImportSnapshotFile,
    ExportSnapshotFile,
    RenameSnapshot,
    DeleteSnapshot,
    DeleteAllSnapshots,
    SyncPendingSnapshots,
    GetOperationProgress,
    UpdateSnapshotCameraStations,
    GetCameraReferenceImage
}

public sealed record LocalControlRequest(
    Guid RequestId,
    LocalControlMethod Method,
    JsonElement Payload);

public sealed record LocalControlResponse(
    Guid RequestId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Result);

public sealed record LocalApplicationState(
    ApplicationKind Application,
    bool AdapterAvailable,
    bool IsRunning,
    bool IsStreaming,
    bool IsRecording,
    bool CanDetermineLiveState,
    string Version,
    string StatusMessage);

public sealed record LocalSnapshotSummary(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    long Length,
    bool Uploaded,
    bool UploadEligible,
    Guid? RoomId = null);

public enum LocalOperationKind
{
    Capture,
    Restore
}

public enum LocalOperationStatus
{
    Running,
    Succeeded,
    Blocked,
    Failed,
    FailedRolledBack,
    RollbackFailed
}

public sealed record LocalOperationSummary(
    Guid Id,
    LocalOperationKind Kind,
    LocalOperationStatus Status,
    string Message,
    Guid? SnapshotId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record LocalOperationProgress(bool IsBusy, string Message);

public sealed record LocalAgentState(
    string MachineName,
    bool IsCloudEnrolled,
    bool CanCapture,
    bool CanRestore,
    bool IsBusy,
    bool AutoStartEnabled,
    string StatusMessage,
    string? LanSharedDirectory,
    string LanSyncStatus,
    IReadOnlyList<LocalApplicationState> Applications,
    IReadOnlyList<LocalSnapshotSummary> Snapshots,
    IReadOnlyList<LocalOperationSummary> Operations);

public sealed record CaptureLocalSnapshotRequest(
    string Name,
    IReadOnlyList<CameraStationSnapshot>? CameraStations = null,
    IReadOnlyList<CameraReferenceImageChange>? ImageChanges = null);

public sealed record UpdateSnapshotCameraStationsRequest(
    Guid SnapshotId,
    IReadOnlyList<CameraStationSnapshot> CameraStations,
    IReadOnlyList<CameraReferenceImageChange>? ImageChanges = null);

public sealed record CameraReferenceImageChange(
    int Slot,
    string? SourcePath,
    string? ExpectedSha256,
    bool Remove);

public sealed record RestoreLocalSnapshotRequest(
    Guid SnapshotId,
    IReadOnlyList<CameraStationSnapshot>? CurrentCameraStations = null);

public sealed record DeleteLocalSnapshotRequest(Guid SnapshotId);

public sealed record RenameLocalSnapshotRequest(Guid SnapshotId, string Name);

public sealed record ConfigureObsRequest(Uri Endpoint, string Password);

public sealed record ConfigureLanDirectoryRequest(string? Path);

public sealed record ConfigureAutoStartRequest(bool Enabled);

public sealed record EnrollLocalDeviceRequest(Uri ServiceUri, string EnrollmentToken, string DeviceName);

public sealed record GetLocalMappingContextRequest(Guid SnapshotId);

public sealed record GetLocalSnapshotDetailRequest(Guid SnapshotId);

public sealed record GetLocalSnapshotPreviewRequest(Guid SnapshotId, ApplicationKind Application);

public sealed record GetCameraReferenceImageRequest(Guid SnapshotId, int Slot);

public sealed record LocalSnapshotPreview(
    bool Found,
    string MediaType,
    byte[] Content);

public sealed record SaveLocalDeviceMappingRequest(
    Guid SnapshotId,
    Guid SourceLogicalId,
    ApplicationKind Application,
    string TargetDeviceId,
    string TargetSourceName);

public sealed record LocalMappingSource(
    Guid SourceLogicalId,
    ApplicationKind Application,
    string SourceName,
    string DeviceName,
    VideoMode? RequiredMode,
    DeviceMapping? Mapping);

public sealed record LocalMappingTarget(
    ApplicationKind Application,
    string SourceName,
    string TargetDeviceId,
    string DeviceName,
    VideoMode? CurrentMode);

public sealed record LocalMappingContext(
    Guid SnapshotId,
    IReadOnlyList<LocalMappingSource> Sources,
    IReadOnlyList<LocalMappingTarget> Targets);

public sealed record SnapshotFileRequest(string Path);

public sealed record ImportSnapshotFileRequest(string Path, bool TrustSigner);

public sealed record ExportSnapshotFileRequest(Guid SnapshotId, string Path);

public sealed record SnapshotImportPreview(
    Guid SnapshotId,
    string Name,
    DateTimeOffset CreatedAt,
    string SignerKeyId,
    string SignerFingerprintSha256,
    bool SignerTrusted);

public sealed record SnapshotTransferResult(
    Guid SnapshotId,
    string Name,
    string Path);

public sealed record LocalSnapshotOperationResult(Guid SnapshotId, string Name, DateTimeOffset CompletedAt);

public sealed record DeleteSnapshotsResult(int DeletedCount);

public sealed record SnapshotSyncResult(int UploadedCount, int RemainingCount, string Message);

public static class LocalControlProtocol
{
    public const string PipeName = "LiveStudio.Agent.Control";
    // Complete LiveCompanion native trees can exceed 4 MB before packaging. The pipe is
    // restricted to the current Windows user, but still keeps a finite allocation limit.
    public const int MaximumMessageLength = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static LocalControlRequest CreateRequest<T>(LocalControlMethod method, T payload) => new(
        Guid.NewGuid(),
        method,
        JsonSerializer.SerializeToElement(payload, JsonOptions));

    public static LocalControlResponse CreateSuccess<T>(Guid requestId, T result) => new(
        requestId,
        true,
        null,
        null,
        JsonSerializer.SerializeToElement(result, JsonOptions));

    public static LocalControlResponse CreateFailure(
        Guid requestId,
        string errorCode,
        string errorMessage) => new(
        requestId,
        false,
        errorCode,
        errorMessage,
        null);

    public static T DeserializePayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new InvalidDataException($"无法解析本机控制参数 {typeof(T).Name}");

    public static T DeserializeResult<T>(LocalControlResponse response)
    {
        if (!response.Success)
        {
            throw new LocalControlException(
                response.ErrorCode ?? "AgentError",
                response.ErrorMessage ?? "本机执行端返回未知错误");
        }

        if (response.Result is not { } result)
        {
            throw new InvalidDataException($"本机执行端没有返回 {typeof(T).Name}");
        }

        var value = result.Deserialize<T>(JsonOptions);
        return value is null
            ? throw new InvalidDataException($"无法解析本机执行端返回的 {typeof(T).Name}")
            : value;
    }

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (content.Length > MaximumMessageLength)
        {
            throw new InvalidDataException("本机控制消息超过长度限制");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, content.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageLength)
        {
            throw new InvalidDataException("本机控制消息长度无效");
        }

        var content = new byte[length];
        await stream.ReadExactlyAsync(content, cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonOptions)
            ?? throw new InvalidDataException($"无法解析本机控制消息 {typeof(T).Name}");
    }
}

public sealed class LocalControlException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
