using LiveStudio.Cloud.Features.Jobs;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class JobTransitionRulesTests
{
    [Fact]
    public void CaptureJobSupportsCompleteExecutionPath()
    {
        var path = new[]
        {
            JobStatus.Queued,
            JobStatus.Claimed,
            JobStatus.Preflight,
            JobStatus.Capturing,
            JobStatus.Packaging,
            JobStatus.Uploading,
            JobStatus.Succeeded
        };

        for (var index = 0; index < path.Length - 1; index++)
        {
            Assert.True(JobTransitionRules.CanTransition(path[index], path[index + 1]));
        }
    }

    [Fact]
    public void CaptureJobCannotSkipPackageVerification()
    {
        Assert.False(JobTransitionRules.CanTransition(JobStatus.Capturing, JobStatus.Succeeded));
        Assert.False(JobTransitionRules.CanTransition(JobStatus.Packaging, JobStatus.Succeeded));
    }

    [Fact]
    public void PreviewRefreshUsesDedicatedReadOnlyPath()
    {
        Assert.True(JobTransitionRules.CanTransition(JobStatus.Preflight, JobStatus.RefreshingPreview));
        Assert.True(JobTransitionRules.CanTransition(JobStatus.RefreshingPreview, JobStatus.Succeeded));
        Assert.False(JobTransitionRules.CanTransition(JobStatus.RefreshingPreview, JobStatus.Applying));
    }
}
