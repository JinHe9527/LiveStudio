using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Agent.Tests;

public sealed class RestoreFinalizationTests
{
    [Fact]
    public async Task CancellationAfterCommitStillPersistsTerminalResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var persisted = false;
        var result = await RestoreFinalization.RunAsync(
            new RestoreExecutionResult(JobStatus.Succeeded, "已恢复", []), token =>
            {
                Assert.False(token.CanBeCanceled);
                persisted = true;
                return Task.CompletedTask;
            }, token => Task.FromCanceled(token), cancellation.Token);

        Assert.True(persisted);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Differences);
        Assert.Contains("预览同步未完成", result.Message);
    }

    [Fact]
    public async Task HousekeepingFailuresCannotChangeCommittedOutcome()
    {
        var result = await RestoreFinalization.RunAsync(
            new RestoreExecutionResult(JobStatus.Succeeded, "已恢复", []),
            _ => Task.FromException(new IOException("database unavailable")),
            _ => Task.FromException(new IOException("network unavailable")), CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Differences.Count);
        Assert.Contains("记录未能保存", result.Message);
        Assert.Contains("预览同步未完成", result.Message);
    }

    [Theory]
    [InlineData(JobStatus.FailedRolledBack)]
    [InlineData(JobStatus.RollbackFailed)]
    public async Task FailedRestorePreservesOutcomeAndDoesNotPublishPreview(JobStatus status)
    {
        var published = false;
        var result = await RestoreFinalization.RunAsync(
            new RestoreExecutionResult(status, "恢复失败", ["原始差异"]),
            _ => Task.FromException(new IOException("database unavailable")), _ =>
            {
                published = true;
                return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.False(published);
        Assert.Equal("原始差异", result.Differences[0]);
        Assert.Equal(2, result.Differences.Count);
    }

    [Fact]
    public async Task SuccessfulFinalizationRetainsOriginalResult()
    {
        var original = new RestoreExecutionResult(JobStatus.Succeeded, "已恢复", []);
        var result = await RestoreFinalization.RunAsync(original,
            _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);

        Assert.Same(original, result);
    }
}
