using LiveStudio.Contracts;

namespace LiveStudio.Core;

public sealed record ApplicationRuntimeStatus(
    bool IsRunning,
    bool IsStreaming,
    bool IsRecording,
    string Version,
    bool CanDetermineLiveState);

public sealed record RestorePreflightResult(
    bool CanProceed,
    JobStatus FailureStatus,
    string Message)
{
    public static RestorePreflightResult Success { get; } = new(true, JobStatus.Preflight, string.Empty);

    public static RestorePreflightResult Fail(JobStatus status, string message) => new(false, status, message);
}

public sealed record RestoreVerificationResult(
    bool IsMatch,
    IReadOnlyList<string> Differences);

public sealed record PreviewCapture(
    ApplicationKind Application,
    string MediaType,
    ReadOnlyMemory<byte> Content,
    DateTimeOffset CapturedAt);

public sealed record RestoreExecutionContext(
    Guid JobId,
    ApplicationSnapshot Snapshot,
    IReadOnlyList<DeviceMapping> Mappings,
    bool IsUnattended,
    string AssetDirectory);

public interface IApplicationAdapter
{
    ApplicationKind Kind { get; }

    Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken);

    Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken);

    Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken);

    Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken);

    Task<RestorePreflightResult> PreflightAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken);

    Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IApplicationRestoreSession : IAsyncDisposable
{
    ApplicationKind Kind { get; }

    Task StopAsync(CancellationToken cancellationToken);

    Task ApplyAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public sealed record RestoreExecutionResult(
    JobStatus Status,
    string Message,
    IReadOnlyList<string> Differences)
{
    public bool IsSuccess => Status == JobStatus.Succeeded;
}
