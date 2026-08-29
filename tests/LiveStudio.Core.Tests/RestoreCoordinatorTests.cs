using LiveStudio.Contracts;
using LiveStudio.Core;
using System.ComponentModel;
using System.Diagnostics;

namespace LiveStudio.Core.Tests;

public sealed class RestoreCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsyncDoesNotConsultLiveStateBeforeRestore()
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

        Assert.True(result.IsSuccess);
        Assert.Equal(0, adapter.InspectCount);
        Assert.Equal(1, adapter.BeginRestoreCount);
        Assert.Equal(1, prepareAssetsCount);
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

    [Theory]
    [InlineData(RestoreFault.BeginRestore)]
    [InlineData(RestoreFault.Stop)]
    [InlineData(RestoreFault.Apply)]
    [InlineData(RestoreFault.Start)]
    [InlineData(RestoreFault.Verify)]
    [InlineData(RestoreFault.Commit)]
    public async Task ExecuteAsyncRollsBackAllCreatedSessionsWhenAnyTransactionStageThrows(
        RestoreFault fault)
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion) { Fault = fault };
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
        Assert.Equal(fault != RestoreFault.BeginRestore, companion.Session.WasRolledBack);
        if (fault == RestoreFault.Commit)
        {
            Assert.True(obs.Session.WasCommitted);
            Assert.True(obs.Session.WasRolledBack);
        }
    }

    [Fact]
    public async Task ExecuteAsyncStopsBeforeCreatingTransactionsWhenPreflightFails()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs)
        {
            Preflight = RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "目标版本不匹配")
        };
        var coordinator = new RestoreCoordinator([obs]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.IncompatibleVersion, result.Status);
        Assert.Equal(0, obs.BeginRestoreCount);
        Assert.False(obs.Session.WasStopped);
        Assert.False(obs.Session.WasRolledBack);
    }

    [Fact]
    public async Task ExecuteAsyncCreatesPersistentBackupBeforeTransactionSessions()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs);
        var coordinator = new RestoreCoordinator([adapter]);
        var backupCount = 0;

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            _ =>
            {
                Assert.Equal(0, adapter.BeginRestoreCount);
                backupCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, backupCount);
        Assert.Equal(1, adapter.BeginRestoreCount);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCreateTransactionWhenPersistentBackupFails()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs);
        var coordinator = new RestoreCoordinator([adapter]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromException(new IOException("自动备份失败")),
            CancellationToken.None);

        Assert.Equal(JobStatus.FailedRolledBack, result.Status);
        Assert.Equal(0, adapter.BeginRestoreCount);
        Assert.False(adapter.Session.WasStopped);
        Assert.False(adapter.Session.WasRolledBack);
        Assert.Contains("恢复前自动备份失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncIdentifiesElevatedApplicationAndRollsBackWhenStopIsDenied()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion)
        {
            Fault = RestoreFault.StopAccessDenied
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
        Assert.Contains("停止抖音直播伴侣失败", result.Message, StringComparison.Ordinal);
        Assert.Contains("拒绝访问", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(obs.Session.WasRolledBack);
        Assert.True(companion.Session.WasRolledBack);
    }

    [Fact]
    public void WindowsProcessTerminatorRecognizesOnlyAccessDeniedAsElevationCase()
    {
        Assert.True(WindowsProcessTerminator.RequiresElevation(new UnauthorizedAccessException()));
        Assert.True(WindowsProcessTerminator.RequiresElevation(new Win32Exception(5)));
        Assert.True(WindowsProcessTerminator.RequiresElevation(
            new InvalidOperationException("wrapped", new Win32Exception(5))));
        Assert.False(WindowsProcessTerminator.RequiresElevation(new Win32Exception(2)));
    }

    [Fact]
    public void WindowsProcessTerminatorUsesOnlySignedSystemTaskKillForElevation()
    {
        var startInfo = WindowsProcessTerminator.CreateElevatedStartInfo(1234);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), startInfo.FileName);
        Assert.Equal("/PID 1234 /T /F", startInfo.Arguments);
        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }

    [Fact]
    public async Task ExecuteAsyncRollsBackWhenCancellationOccursAfterTransactionBegins()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs)
        {
            Fault = RestoreFault.ApplyCancellation
        };
        var coordinator = new RestoreCoordinator([obs]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.FailedRolledBack, result.Status);
        Assert.True(obs.Session.WasStopped);
        Assert.True(obs.Session.WasRolledBack);
        Assert.False(obs.Session.WasCommitted);
    }

    [Fact]
    public async Task ExecuteAsyncKeepsDurablyCommittedSessionsWhenFinalProgressPersistenceFails()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion);
        var coordinator = new RestoreCoordinator([obs, companion]);

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs, ApplicationKind.LiveCompanion),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (status, _, _) => status == JobStatus.Succeeded
                ? Task.FromException(new IOException("最终状态写入失败"))
                : Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.True(obs.Session.WasCommitted);
        Assert.True(companion.Session.WasCommitted);
        Assert.True(obs.Session.WasCompleted);
        Assert.True(companion.Session.WasCompleted);
        Assert.False(obs.Session.WasRolledBack);
        Assert.False(companion.Session.WasRolledBack);
    }

    [Theory]
    [InlineData(RestoreFaultPoint.SessionsCreated)]
    [InlineData(RestoreFaultPoint.ApplicationsStopped)]
    [InlineData(RestoreFaultPoint.AssetsPrepared)]
    [InlineData(RestoreFaultPoint.SettingsApplied)]
    [InlineData(RestoreFaultPoint.ApplicationsStarted)]
    [InlineData(RestoreFaultPoint.VerificationPassed)]
    [InlineData(RestoreFaultPoint.ApplicationsCommitted)]
    public async Task ExecuteAsyncRollsBackAtEveryExplicitValidationBoundary(RestoreFaultPoint point)
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion);
        var coordinator = new RestoreCoordinator(
            [obs, companion],
            new ThrowingFaultInjector(point));

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
        if (point == RestoreFaultPoint.ApplicationsCommitted)
        {
            Assert.True(obs.Session.WasCommitted);
            Assert.True(companion.Session.WasCommitted);
        }
    }

    [Fact]
    public async Task ExecuteAsyncNeverRollsBackAfterDurableCommitDecision()
    {
        var obs = new FakeAdapter(ApplicationKind.Obs);
        var companion = new FakeAdapter(ApplicationKind.LiveCompanion);
        var coordinator = new RestoreCoordinator(
            [obs, companion],
            new ThrowingFaultInjector(RestoreFaultPoint.DurableCommitRecorded));
        var jobId = Guid.NewGuid();

        var result = await coordinator.ExecuteAsync(
            jobId,
            CreateSnapshot(ApplicationKind.Obs, ApplicationKind.LiveCompanion),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.True(obs.Session.WasCommitted);
        Assert.True(companion.Session.WasCommitted);
        Assert.False(obs.Session.WasRolledBack);
        Assert.False(companion.Session.WasRolledBack);
        Assert.True(await RestoreTransactionJournal.IsCommittedAsync(jobId, CancellationToken.None));
        await RestoreTransactionJournal.CompleteAsync(jobId, CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsyncSerializesConcurrentRestoreRequests()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs);
        var coordinator = new RestoreCoordinator([adapter]);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            async (status, _, _) =>
            {
                if (status == JobStatus.Preflight)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }
            },
            CancellationToken.None);
        await firstEntered.Task;

        var second = coordinator.ExecuteAsync(
            Guid.NewGuid(),
            CreateSnapshot(ApplicationKind.Obs),
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (status, _, _) =>
            {
                if (status == JobStatus.Preflight)
                {
                    secondEntered.TrySetResult();
                }

                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Task.Delay(100);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompleted);
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
    public async Task ExecuteAsyncRollsBackTransactionWhenAssetMaterializationFails()
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
        Assert.Equal(1, adapter.BeginRestoreCount);
        Assert.True(adapter.Session.WasStopped);
        Assert.True(adapter.Session.WasRolledBack);
        Assert.False(adapter.Session.WasCommitted);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsSchemaTwoBeforeInspectingApplications()
    {
        var adapter = new FakeAdapter(ApplicationKind.Obs);
        var coordinator = new RestoreCoordinator([adapter]);
        var snapshot = CreateSnapshot(ApplicationKind.Obs) with { SchemaVersion = 2 };

        var result = await coordinator.ExecuteAsync(
            Guid.NewGuid(),
            snapshot,
            [],
            false,
            "/tmp/assets",
            _ => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(JobStatus.IncompatibleVersion, result.Status);
        Assert.Equal(0, adapter.BeginRestoreCount);
        Assert.Contains("只允许查看", result.Message, StringComparison.Ordinal);
    }

    private static CombinedSnapshot CreateSnapshot(params ApplicationKind[] applications) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "测试存档",
        DateTimeOffset.UtcNow,
        3,
        applications.Select(application => new ApplicationSnapshot(
            application,
            "1.0.0",
            "test-adapter",
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            "fingerprint",
            CompatibilityLevel.Verified,
            true,
            [],
            [],
            [])).ToArray(),
        [],
        []);

    private sealed class FakeAdapter(ApplicationKind kind) : IApplicationAdapter
    {
        public ApplicationKind Kind { get; } = kind;

        public ApplicationRuntimeStatus RuntimeStatus { get; init; } = new(true, false, false, "1.0.0", true);

        public RestoreVerificationResult Verification { get; init; } = new(true, []);

        public RestorePreflightResult Preflight { get; init; } = RestorePreflightResult.Success;

        public RestoreFault Fault { get; init; }

        public FakeSession Session { get; } = new(kind);

        public int BeginRestoreCount { get; private set; }

        public int InspectCount { get; private set; }

        public Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken)
        {
            InspectCount++;
            return Task.FromResult(RuntimeStatus);
        }

        public Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationSnapshot(
                Kind,
                RuntimeStatus.Version,
                "test-adapter",
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                "fingerprint",
                CompatibilityLevel.Verified,
                RuntimeStatus.IsRunning,
                [],
                [],
                []));

        public Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken) =>
            CaptureAsync(cancellationToken);

        public Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PreviewCapture?>(null);

        public Task<RestorePreflightResult> PreflightAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(Preflight);

        public Task<IApplicationRestoreSession> BeginRestoreAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken)
        {
            BeginRestoreCount++;
            if (Fault == RestoreFault.BeginRestore)
            {
                return Task.FromException<IApplicationRestoreSession>(
                    new InvalidOperationException("注入 BeginRestore 故障"));
            }

            Session.Verification = Verification;
            Session.Fault = Fault;
            return Task.FromResult<IApplicationRestoreSession>(Session);
        }
    }

    private sealed class FakeSession(ApplicationKind kind) : IApplicationRestoreSession
    {
        public ApplicationKind Kind { get; } = kind;

        public RestoreVerificationResult Verification { get; set; } = new(true, []);

        public RestoreFault Fault { get; set; }

        public bool WasCommitted { get; private set; }

        public bool WasRolledBack { get; private set; }

        public bool WasStopped { get; private set; }

        public bool WasCompleted { get; private set; }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            WasStopped = true;
            return FailAt(RestoreFault.Stop);
        }

        public Task ApplyAsync(CancellationToken cancellationToken) => Fault == RestoreFault.ApplyCancellation
            ? Task.FromException(new OperationCanceledException("注入 Apply 取消"))
            : FailAt(RestoreFault.Apply);

        public Task StartAsync(CancellationToken cancellationToken) => FailAt(RestoreFault.Start);

        public Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
            Fault == RestoreFault.Verify
                ? Task.FromException<RestoreVerificationResult>(
                    new InvalidOperationException("注入 Verify 故障"))
                : Task.FromResult(Verification);

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (Fault == RestoreFault.Commit)
            {
                return Task.FromException(new InvalidOperationException("注入 Commit 故障"));
            }

            WasCommitted = true;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            WasCompleted = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            WasRolledBack = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task FailAt(RestoreFault stage) => Fault == stage
            ? Task.FromException(new InvalidOperationException($"注入 {stage} 故障"))
            : Fault == RestoreFault.StopAccessDenied && stage == RestoreFault.Stop
                ? Task.FromException(new UnauthorizedAccessException("拒绝访问"))
            : Task.CompletedTask;
    }

    public enum RestoreFault
    {
        None,
        BeginRestore,
        Stop,
        Apply,
        Start,
        Verify,
        Commit,
        ApplyCancellation,
        StopAccessDenied
    }

    private sealed class ThrowingFaultInjector(RestoreFaultPoint selectedPoint) : IRestoreFaultInjector
    {
        public Task InjectAsync(RestoreFaultPoint point, CancellationToken cancellationToken) =>
            point == selectedPoint
                ? Task.FromException(new InvalidOperationException($"注入边界故障 {point}"))
                : Task.CompletedTask;
    }
}
