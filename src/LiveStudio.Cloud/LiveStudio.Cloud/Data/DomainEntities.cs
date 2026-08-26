using LiveStudio.Contracts;

namespace LiveStudio.Cloud.Data;

public sealed class OrganizationEntity
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrganizationMemberEntity
{
    public Guid OrganizationId { get; set; }

    public required string UserId { get; set; }

    public OrganizationRole Role { get; set; }
}

public sealed class LiveRoomEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    public Guid? DeviceId { get; set; }

    public DateTimeOffset? LastSnapshotAt { get; set; }
}

public sealed class ManagedDeviceEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public required string Name { get; set; }

    public required string MachineName { get; set; }

    public required string AgentVersion { get; set; }

    public required string OperatingSystem { get; set; }

    public required string ApplicationVersionsJson { get; set; }

    public required string CapabilitiesJson { get; set; }

    public required string PackageSigningPublicKeyPem { get; set; }

    public required byte[] DeviceKeyHash { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public bool InteractiveUserSession { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class DeviceEnrollmentEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public required string DeviceName { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class DesktopAuthorizationSessionEntity
{
    public Guid Id { get; set; }

    public required string DeviceName { get; set; }

    public required string UserCode { get; set; }

    public required byte[] DeviceCodeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? ApprovedByUserId { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public string? IssuedTokenProtected { get; set; }

    public DateTimeOffset? IssuedTokenExpiresAt { get; set; }
}

public sealed class DesktopAccessTokenEntity
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public required string DeviceName { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class CurrentParameterStateEntity
{
    public Guid DeviceId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    public required string ParameterHash { get; set; }

    public required string ParametersJson { get; set; }

    public string? ObsPreviewObjectKey { get; set; }

    public string? LiveCompanionPreviewObjectKey { get; set; }
}

public sealed class DeviceCapabilityEntity
{
    public Guid DeviceId { get; set; }

    public Guid OrganizationId { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    public required string CapabilityJson { get; set; }
}

public sealed class DeviceHeartbeatEntity
{
    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }

    public Guid OrganizationId { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public bool InteractiveUserSession { get; set; }

    public required string AgentVersion { get; set; }

    public required string ApplicationVersionsJson { get; set; }
}

public sealed class SnapshotEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public required string Name { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string PackageObjectKey { get; set; }

    public long PackageLength { get; set; }

    public required string PackageSha256 { get; set; }

    public required string ParameterHash { get; set; }

    public required string ManifestJson { get; set; }
}

public sealed class SnapshotUploadEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public required string Name { get; set; }

    public required string CreatedBy { get; set; }

    public required string ObjectKey { get; set; }

    public required string MultipartUploadId { get; set; }

    public required string ExpectedSha256 { get; set; }

    public long ExpectedLength { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class SnapshotComponentEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid SnapshotId { get; set; }

    public ApplicationKind Application { get; set; }

    public required string ApplicationVersion { get; set; }

    public required string ParametersJson { get; set; }

    public string? PreviewObjectKey { get; set; }
}

public sealed class AssetEntity
{
    public Guid OrganizationId { get; set; }

    public required string Sha256 { get; set; }

    public long Length { get; set; }

    public required string MediaType { get; set; }

    public required string ObjectKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SnapshotAssetEntity
{
    public Guid OrganizationId { get; set; }

    public Guid SnapshotId { get; set; }

    public required string Sha256 { get; set; }
}

public sealed class ObjectDeletionEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public required string ObjectKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

public sealed class DeviceMappingEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid SourceLogicalId { get; set; }

    public ApplicationKind Application { get; set; }

    public required string TargetDeviceId { get; set; }

    public required string TargetSourceName { get; set; }

    public required string TargetSceneName { get; set; }

    public bool CreateSourceWhenMissing { get; set; }
}

public sealed class RemoteJobEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RoomId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid? SnapshotId { get; set; }

    public JobKind Kind { get; set; }

    public JobStatus Status { get; set; }

    public CompatibilityLevel Compatibility { get; set; }

    public required string RequestedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public Guid? ExecutionId { get; set; }

    public long LastEventSequence { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Message { get; set; }

    public string? DetailCode { get; set; }
}

public sealed class JobEventEntity
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public Guid ExecutionId { get; set; }

    public long Sequence { get; set; }

    public JobStatus Status { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string Message { get; set; }

    public string? DetailCode { get; set; }

    public string? VerificationDetail { get; set; }
}

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public required string ActorId { get; set; }

    public required string Action { get; set; }

    public required string TargetType { get; set; }

    public required string TargetId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string DetailJson { get; set; }
}

public sealed class AdapterCatalogEntity
{
    public Guid Id { get; set; }

    public ApplicationKind Application { get; set; }

    public required string MinimumVersion { get; set; }

    public required string MaximumVersion { get; set; }

    public required string StructureFingerprint { get; set; }

    public required string DefinitionObjectKey { get; set; }

    public required string DefinitionSha256 { get; set; }

    public required string Signature { get; set; }

    public bool Verified { get; set; }

    public DateTimeOffset PublishedAt { get; set; }
}
