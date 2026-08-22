using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Core.Tests;

public sealed class RestoreCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsyncBlocksBeforeCreatingSessionWhenApplicationIsLive()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs)
        {
            RuntimeStatus = new(true, true, false, "32.0.0", true)
        };
        var coordinator = new RestoreCoordinator([adapter]);
        var prepareAssetsCount = 0;

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            true,
            "/tmp/assets",
            _ =>
            {
                prepareAssetsCount++;
                return Task.CompletedTask;
            },
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.BlockedByLiveSession, result.Status);
        Assert.Equal(0, adapter.BeginRestoreCount);
        Assert.Equal(0, prepareAssetsCount);
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackEveryApplicationWhenVerificationFails()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion)
        {
            Verification = new(false, ["滤镜强度不一致"])
        };
        var coordinator = new RestoreCoordinator([obs, companion]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs, ApplicationKind.LiveCompanion),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.FailedRolledBack, result.Status);
        Assert.True(obs.Session.WasRolledBack);
        Assert.True(companion.Session.WasRolledBack);
        Assert.False(obs.Session.WasCommitted);
        Assert.Contains(result.Differences, difference => difference.Contains("滤镜强度", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsyncCommitsOnlyAfterEveryApplicationMatches()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion);
        var statuses = new List<JobStatus>();
        var coordinator = new RestoreCoordinator([obs, companion]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs, ApplicationKind.LiveCompanion),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (status, _, _) =>
            {
                statuses.Add(status);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(obs.Session.WasCommitted);
        Assert.True(companion.Session.WasCommitted);
        Assert.False(obs.Session.WasRolledBack);
        Assert.Equal(JobStatus.Succeeded, statuses[^1]);
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackWhenAssetMaterializationFails()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs);
        var coordinator = new RestoreCoordinator([adapter]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.FromException(new IOException("素材目录写入失败")),
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.FailedRolledBack, result.Status);
        Assert.True(adapter.Session.WasStopped);
        Assert.True(adapter.Session.WasRolledBack);
        Assert.False(adapter.Session.WasCommitted);
    }

    private static CombinedSnapshot CreateSnapshot(params ApplicationKind[] applications) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "测试存档",
        DateTimeOffset.UtcNow,
        1,
        applications.Select(application => new ApplicationSnapshot(application, "1.0.0", "fingerprint", [], [])).ToArray(),
        [],
        []);

    private sealed class FakeAdapter(ApplicationKind kind) : IApplicationAdapter
    {
        public ApplicationKind Kind { get; } = kind;

        public ApplicationRuntimeStatus RuntimeStatus { get; init; } = new(true, false, false, "1.0.0", true);

        public RestoreVerificationResult Verification { get; init; } = new(true, []);

        public FakeSession Session { get; } = new(kind);

        public int BeginRestoreCount { get; private set; }

        public Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RuntimeStatus);

        public Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationSnapshot(Kind, RuntimeStatus.Version, "fingerprint", [], []));

        public Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken) =>
            CaptureAsync(cancellationToken);

        public Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PreviewCapture?>(null);

        public Task<RestorePreflightResult> PreflightAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(RestorePreflightResult.Success);

        public Task<IApplicationRestoreSession> BeginRestoreAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken)
        {
            BeginRestoreCount++;
            Session.Verification = Verification;
            return Task.FromResult<IApplicationRestoreSession>(Session);
        }
    }

    private sealed class FakeSession(ApplicationKind kind) : IApplicationRestoreSession
    {
        public ApplicationKind Kind { get; } = kind;

        public RestoreVerificationResult Verification { get; set; } = new(true, []);

        public bool WasCommitted { get; private set; }

        public bool WasRolledBack { get; private set; }

        public bool WasStopped { get; private set; }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            WasStopped = true;
            return Task.CompletedTask;
        }

        public Task ApplyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Verification);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            WasCommitted = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            WasRolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
