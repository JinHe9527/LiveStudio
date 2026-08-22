namespace LiveStudio.Contracts;

public enum OrganizationRole
{
    Owner,
    Admin,
    Operator,
    Viewer
}

public sealed record OrganizationSummary(Guid Id, string Name);

public sealed record MembershipSummary(string UserId, string Email, OrganizationRole Role);

public sealed record AuditEventSummary(
    Guid Id,
    string ActorId,
    string Action,
    string TargetType,
    string TargetId,
    DateTimeOffset OccurredAt);

public sealed record AddMembershipRequest(string Email, OrganizationRole Role);

public sealed record LiveRoomSummary(
    Guid Id,
    Guid OrganizationId,
    string Name,
    Guid? DeviceId,
    DateTimeOffset? LastSnapshotAt,
    bool Online,
    bool HasConfigurationDrift);

public sealed record DeviceHeartbeat(
    Guid DeviceId,
    Guid OrganizationId,
    DateTimeOffset SentAt,
    string AgentVersion,
    string OperatingSystem,
    bool InteractiveUserSession,
    IReadOnlyDictionary<ApplicationKind, string> ApplicationVersions);

public sealed record DeviceCapability(
    Guid DeviceId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<CaptureDeviceDescriptor> CaptureDevices,
    IReadOnlyDictionary<ApplicationKind, IReadOnlyList<string>> AvailableFilterKinds,
    IReadOnlyList<DeviceVideoSourceCapability> VideoSources);

public sealed record DeviceVideoSourceCapability(
    ApplicationKind Application,
    Guid SourceLogicalId,
    string SourceName,
    CaptureDeviceDescriptor Device,
    VideoMode? CurrentMode);

public sealed record DeviceManagementState(
    DeviceSummary Device,
    DeviceCapability? Capabilities,
    CurrentParameterState? CurrentParameters,
    IReadOnlyDictionary<ApplicationKind, Uri> CurrentPreviewUrls);

public sealed record DeviceSummary(
    Guid Id,
    Guid RoomId,
    string Name,
    string MachineName,
    string AgentVersion,
    string OperatingSystem,
    DateTimeOffset LastSeenAt,
    bool InteractiveUserSession,
    bool Online);

public sealed record AdapterCatalogSummary(
    Guid Id,
    ApplicationKind Application,
    string MinimumVersion,
    string MaximumVersion,
    string StructureFingerprint,
    bool Verified,
    DateTimeOffset PublishedAt);
