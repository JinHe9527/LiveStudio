namespace LiveStudio.Contracts;

public sealed record CreateOrganizationRequest(string Name);

public sealed record CreateRoomRequest(string Name);

public sealed record CreateDeviceEnrollmentRequest(Guid RoomId, string DeviceName);

public sealed record DeviceEnrollmentResponse(Guid EnrollmentId, string EnrollmentToken, DateTimeOffset ExpiresAt);

public sealed record EnrollDeviceRequest(
    string EnrollmentToken,
    string MachineName,
    string AgentVersion,
    string OperatingSystem,
    string PackageSigningPublicKeyPem);

public sealed record EnrollDeviceResponse(Guid DeviceId, Guid OrganizationId, Guid RoomId, string DeviceSecret);

public sealed record HeartbeatRequest(
    string AgentVersion,
    string OperatingSystem,
    bool InteractiveUserSession,
    IReadOnlyDictionary<ApplicationKind, string> ApplicationVersions,
    DeviceCapability? Capabilities);

public sealed record CurrentStateRequest(
    CurrentParameterState State,
    IReadOnlyList<CurrentPreviewUpload> Previews,
    CurrentStateReason Reason);

public enum CurrentStateReason
{
    Heartbeat,
    ManualRefresh,
    RemoteRefresh,
    Restore
}

public sealed record CurrentPreviewUpload(
    ApplicationKind Application,
    string MediaType,
    string ContentBase64);

public sealed record SaveDeviceMappingRequest(
    Guid SourceLogicalId,
    ApplicationKind Application,
    string TargetDeviceId,
    string TargetSourceName,
    string TargetSceneName,
    bool CreateSourceWhenMissing);

public sealed record CreateSnapshotUploadRequest(
    Guid RoomId,
    string Name,
    string Sha256,
    long Length);

public sealed record SnapshotUploadResponse(
    Guid UploadId,
    int PartSize,
    DateTimeOffset ExpiresAt);

public sealed record SnapshotUploadPartResponse(
    int PartNumber,
    Uri UploadUri,
    DateTimeOffset ExpiresAt);

public sealed record UploadedSnapshotPart(int PartNumber, string ETag);

public sealed record CompleteSnapshotUploadRequest(IReadOnlyList<UploadedSnapshotPart> Parts);

public sealed record CompleteSnapshotUploadResponse(Guid SnapshotId);

public sealed record SnapshotDownloadResponse(Uri DownloadUri, DateTimeOffset ExpiresAt);

public sealed record SnapshotSummary(
    Guid Id,
    Guid RoomId,
    string Name,
    DateTimeOffset CreatedAt,
    long PackageLength,
    string PackageSha256);

public sealed record SnapshotDetail(
    SnapshotSummary Summary,
    IReadOnlyList<ApplicationSnapshot> Applications,
    IReadOnlyDictionary<ApplicationKind, Uri> PreviewUrls);

public sealed record CreateRestoreJobRequest(
    Guid RoomId,
    Guid DeviceId,
    Guid SnapshotId);

public sealed record CreateCaptureJobRequest(Guid RoomId, Guid DeviceId, string Name);

public sealed record CreateRefreshPreviewJobRequest(Guid RoomId, Guid DeviceId);

public sealed record ClaimJobResponse(
    Guid Id,
    JobKind Kind,
    string Name,
    Guid RoomId,
    Guid DeviceId,
    Guid? SnapshotId,
    CompatibilityLevel Compatibility,
    DateTimeOffset LeaseUntil);

public sealed record AgentSnapshotDownloadResponse(
    Uri DownloadUri,
    DateTimeOffset ExpiresAt,
    string Sha256,
    long Length,
    string SigningKeyId,
    string SigningPublicKeyPem);

public sealed record ReportJobEventRequest(JobStatus Status, string Message, string? DetailCode);
