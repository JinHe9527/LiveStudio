using LiveStudio.Contracts;

namespace LiveStudio.Cloud.Features.Jobs;

public static class JobTransitionRules
{
    private static readonly Dictionary<JobStatus, IReadOnlySet<JobStatus>> AllowedTransitions =
        new Dictionary<JobStatus, IReadOnlySet<JobStatus>>
        {
            [JobStatus.Queued] = new HashSet<JobStatus> { JobStatus.Claimed, JobStatus.DeviceOffline },
            [JobStatus.Claimed] = new HashSet<JobStatus> { JobStatus.Preflight },
            [JobStatus.Preflight] = new HashSet<JobStatus>
            {
                JobStatus.BackingUp,
                JobStatus.Capturing,
                JobStatus.RefreshingPreview,
                JobStatus.BlockedByLiveSession,
                JobStatus.MappingRequired,
                JobStatus.UnsupportedDeviceMode,
                JobStatus.MissingFilter,
                JobStatus.IncompatibleVersion
            },
            [JobStatus.Capturing] = new HashSet<JobStatus>
            {
                JobStatus.Packaging,
                JobStatus.BlockedByLiveSession,
                JobStatus.IncompatibleVersion,
                JobStatus.FailedRolledBack
            },
            [JobStatus.RefreshingPreview] = new HashSet<JobStatus>
            {
                JobStatus.Succeeded,
                JobStatus.IncompatibleVersion
            },
            [JobStatus.Packaging] = new HashSet<JobStatus>
            {
                JobStatus.Uploading,
                JobStatus.FailedRolledBack
            },
            [JobStatus.Uploading] = new HashSet<JobStatus>
            {
                JobStatus.Succeeded,
                JobStatus.FailedRolledBack
            },
            [JobStatus.BackingUp] = new HashSet<JobStatus>
            {
                JobStatus.StoppingApplications,
                JobStatus.FailedRolledBack,
                JobStatus.RollbackFailed
            },
            [JobStatus.StoppingApplications] = new HashSet<JobStatus>
            {
                JobStatus.Applying,
                JobStatus.FailedRolledBack,
                JobStatus.RollbackFailed
            },
            [JobStatus.Applying] = new HashSet<JobStatus>
            {
                JobStatus.StartingApplications,
                JobStatus.FailedRolledBack,
                JobStatus.RollbackFailed
            },
            [JobStatus.StartingApplications] = new HashSet<JobStatus>
            {
                JobStatus.Verifying,
                JobStatus.FailedRolledBack,
                JobStatus.RollbackFailed
            },
            [JobStatus.Verifying] = new HashSet<JobStatus>
            {
                JobStatus.Succeeded,
                JobStatus.FailedRolledBack,
                JobStatus.RollbackFailed
            }
        };

    public static bool CanTransition(JobStatus current, JobStatus next) =>
        AllowedTransitions.TryGetValue(current, out var allowed) && allowed.Contains(next);

    public static bool IsTerminal(JobStatus status) => status is
        JobStatus.Succeeded
        or JobStatus.DeviceOffline
        or JobStatus.BlockedByLiveSession
        or JobStatus.MappingRequired
        or JobStatus.UnsupportedDeviceMode
        or JobStatus.MissingFilter
        or JobStatus.IncompatibleVersion
        or JobStatus.FailedRolledBack
        or JobStatus.RollbackFailed;
}
