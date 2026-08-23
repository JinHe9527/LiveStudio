namespace LiveStudio.Contracts;

public enum JobKind
{
    Capture,
    Restore,
    RefreshPreview
}

public enum JobStatus
{
    Queued,
    Claimed,
    Preflight,
    BackingUp,
    Capturing,
    RefreshingPreview,
    Packaging,
    Uploading,
    StoppingApplications,
    Applying,
    StartingApplications,
    Verifying,
    Succeeded,
    DeviceOffline,
    BlockedByLiveSession,
    MappingRequired,
    UnsupportedDeviceMode,
    MissingFilter,
    IncompatibleVersion,
    FailedRolledBack,
    RollbackFailed
}

public enum CompatibilityLevel
{
    Verified,
    Experimental,
    Unsupported
}

public sealed record CaptureJob(
    Guid Id,
    Guid OrganizationId,
    Guid RoomId,
    Guid DeviceId,
    string Name,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record RestoreJob(
    Guid Id,
    Guid OrganizationId,
    Guid RoomId,
    Guid DeviceId,
    Guid SnapshotId,
    Guid? ExecutionId,
    Guid? LocalTransactionId,
    long LastConfirmedEventSequence,
    CompatibilityLevel Compatibility,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobEvent(
    Guid Id,
    Guid JobId,
    Guid ExecutionId,
    long Sequence,
    JobStatus Status,
    DateTimeOffset OccurredAt,
    string Message,
    string? DetailCode,
    string? VerificationDetail);

public sealed record CompatibilityAssessment(
    ApplicationKind Application,
    string SourceVersion,
    string TargetVersion,
    CompatibilityLevel Level,
    string AdapterId,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<FieldCompatibilityAssessment> Fields);

public sealed record FieldCompatibilityAssessment(
    string NativePath,
    CompatibilityLevel Level,
    string Reason);

public sealed record AgentJobNotification(Guid JobId, JobKind Kind);

public sealed record JobSummary(
    Guid Id,
    Guid RoomId,
    Guid DeviceId,
    Guid? SnapshotId,
    JobKind Kind,
    JobStatus Status,
    CompatibilityLevel Compatibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Message,
    string? DetailCode);
