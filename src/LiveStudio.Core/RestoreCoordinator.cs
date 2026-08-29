using LiveStudio.Contracts;

namespace LiveStudio.Core;

public sealed class RestoreCoordinator(
    IEnumerable<IApplicationAdapter> adapters,
    IRestoreFaultInjector? faultInjector = null,
    ApplicationOperationGate? operationGate = null) : IDisposable
{
    private readonly Dictionary<ApplicationKind, IApplicationAdapter> _adapters = adapters
        .ToDictionary(adapter => adapter.Kind);
    private readonly IRestoreFaultInjector _faultInjector = faultInjector ?? new NoOpRestoreFaultInjector();
    private readonly ApplicationOperationGate _operationGate = operationGate ?? new ApplicationOperationGate();
    private readonly bool _ownsOperationGate = operationGate is null;

    public async Task<RestoreExecutionResult> ExecuteAsync(
        Guid jobId,
        CombinedSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        string assetDirectory,
        Func<CancellationToken, Task> prepareAssets,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken) => await ExecuteAsync(
            jobId,
            snapshot,
            mappings,
            isUnattended,
            assetDirectory,
            prepareAssets,
            reportProgress,
            null,
            cancellationToken);

    public async Task<RestoreExecutionResult> ExecuteAsync(
        Guid jobId,
        CombinedSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        string assetDirectory,
        Func<CancellationToken, Task> prepareAssets,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        Func<CancellationToken, Task>? backupCurrent,
        CancellationToken cancellationToken)
    {
        using var operationLease = await _operationGate.EnterAsync(cancellationToken);
        return await ExecuteCoreAsync(
            jobId,
            snapshot,
            mappings,
            isUnattended,
            assetDirectory,
            prepareAssets,
            reportProgress,
            backupCurrent,
            cancellationToken);
    }

    private async Task<RestoreExecutionResult> ExecuteCoreAsync(
        Guid jobId,
        CombinedSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        string assetDirectory,
        Func<CancellationToken, Task> prepareAssets,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        Func<CancellationToken, Task>? backupCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        ArgumentNullException.ThrowIfNull(prepareAssets);
        ArgumentNullException.ThrowIfNull(reportProgress);

        if (snapshot.SchemaVersion < 3)
        {
            return new RestoreExecutionResult(
                JobStatus.IncompatibleVersion,
                "历史存档缺少完整字段树和证据状态，只允许查看，禁止恢复",
                []);
        }

        await reportProgress(JobStatus.Preflight, "正在检查设备映射和版本兼容性", cancellationToken);

        var contexts = new List<(IApplicationAdapter Adapter, RestoreExecutionContext Context)>();
        var runtimeLeases = new List<IApplicationRuntimeLease>();
        foreach (var originalApplication in snapshot.Applications)
        {
            if (!_adapters.TryGetValue(originalApplication.Kind, out var adapter))
            {
                await DisposeRuntimeLeasesAsync(runtimeLeases);
                return new RestoreExecutionResult(
                    JobStatus.IncompatibleVersion,
                    $"没有可用的 {originalApplication.Kind} 适配器",
                    []);
            }

            ApplicationSnapshot application;
            try
            {
                application = adapter.PrepareRestoreSnapshot(originalApplication);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                await DisposeRuntimeLeasesAsync(runtimeLeases);
                return new RestoreExecutionResult(
                    JobStatus.IncompatibleVersion,
                    exception.Message,
                    []);
            }

            IApplicationRuntimeLease runtimeLease;
            try
            {
                runtimeLease = await adapter.PrepareRuntimeAsync(cancellationToken);
                runtimeLeases.Add(runtimeLease);
            }
            catch
            {
                await DisposeRuntimeLeasesAsync(runtimeLeases);
                throw;
            }

            var context = new RestoreExecutionContext(
                jobId,
                application,
                mappings.Where(mapping => mapping.Application == application.Kind).ToArray(),
                isUnattended,
                assetDirectory,
                runtimeLease.WasRunning);
            RestorePreflightResult preflight;
            try
            {
                preflight = await adapter.PreflightAsync(context, cancellationToken);
            }
            catch
            {
                await DisposeRuntimeLeasesAsync(runtimeLeases);
                throw;
            }

            if (!preflight.CanProceed)
            {
                await DisposeRuntimeLeasesAsync(runtimeLeases);
                return new RestoreExecutionResult(preflight.FailureStatus, preflight.Message, []);
            }

            contexts.Add((adapter, context));
        }

        var sessions = new List<IApplicationRestoreSession>();
        var verificationDifferences = new List<string>();
        var durablyCommitted = false;
        var failureContext = "恢复准备";
        try
        {
            if (backupCurrent is not null)
            {
                failureContext = "恢复前自动备份";
                await reportProgress(JobStatus.BackingUp, "正在保存恢复前自动备份", cancellationToken);
                await backupCurrent(cancellationToken);
            }

            failureContext = "创建目标电脑事务快照";
            await reportProgress(JobStatus.BackingUp, "正在创建目标电脑事务快照", cancellationToken);
            foreach (var item in contexts)
            {
                failureContext = $"创建{DisplayName(item.Adapter.Kind)}事务快照";
                sessions.Add(await item.Adapter.BeginRestoreAsync(item.Context, cancellationToken));
            }
            failureContext = "创建跨应用事务快照";
            await RestoreTransactionJournal.PrepareAsync(jobId, cancellationToken);
            await _faultInjector.InjectAsync(RestoreFaultPoint.SessionsCreated, cancellationToken);

            await reportProgress(JobStatus.StoppingApplications, "正在停止应用并等待配置落盘", cancellationToken);
            foreach (var session in sessions)
            {
                failureContext = $"停止{DisplayName(session.Kind)}";
                await session.StopAsync(cancellationToken);
            }
            failureContext = "停止应用";
            await _faultInjector.InjectAsync(RestoreFaultPoint.ApplicationsStopped, cancellationToken);

            failureContext = "准备滤镜和美颜素材";
            await reportProgress(JobStatus.Applying, "正在校验并物化滤镜和美颜素材", cancellationToken);
            await prepareAssets(cancellationToken);
            await _faultInjector.InjectAsync(RestoreFaultPoint.AssetsPrepared, cancellationToken);

            await reportProgress(JobStatus.Applying, "正在应用设备、画面格式和视频滤镜", cancellationToken);
            foreach (var session in sessions)
            {
                failureContext = $"写入{DisplayName(session.Kind)}配置";
                await session.ApplyAsync(cancellationToken);
            }
            failureContext = "写入画面配置";
            await _faultInjector.InjectAsync(RestoreFaultPoint.SettingsApplied, cancellationToken);

            await reportProgress(JobStatus.StartingApplications, "正在恢复应用运行状态", cancellationToken);
            foreach (var session in sessions)
            {
                failureContext = $"启动{DisplayName(session.Kind)}";
                await session.StartAsync(cancellationToken);
            }
            failureContext = "恢复应用运行状态";
            await _faultInjector.InjectAsync(RestoreFaultPoint.ApplicationsStarted, cancellationToken);

            await reportProgress(JobStatus.Verifying, "正在逐项回读恢复结果", cancellationToken);
            var differences = new List<string>();
            foreach (var session in sessions)
            {
                failureContext = $"回读{DisplayName(session.Kind)}配置";
                var verification = await session.VerifyAsync(cancellationToken);
                if (!verification.IsMatch)
                {
                    differences.AddRange(verification.Differences.Select(value => $"{session.Kind}: {value}"));
                }
            }

            if (differences.Count > 0)
            {
                failureContext = "逐项回读恢复结果";
                verificationDifferences = differences;
                throw new InvalidOperationException("恢复后的参数与存档不一致");
            }
            failureContext = "逐项回读恢复结果";
            await _faultInjector.InjectAsync(RestoreFaultPoint.VerificationPassed, cancellationToken);

            foreach (var session in sessions)
            {
                failureContext = $"提交{DisplayName(session.Kind)}恢复事务";
                await session.CommitAsync(cancellationToken);
            }
            failureContext = "提交跨应用恢复事务";
            await _faultInjector.InjectAsync(RestoreFaultPoint.ApplicationsCommitted, cancellationToken);

            // This is the transaction's point of no return. Until this durable marker exists,
            // every application journal must remain available for startup rollback.
            failureContext = "持久化恢复提交结果";
            await RestoreTransactionJournal.MarkCommittedAsync(jobId, cancellationToken);
            durablyCommitted = true;
            await _faultInjector.InjectAsync(RestoreFaultPoint.DurableCommitRecorded, cancellationToken);
            var cleanupFailed = false;
            foreach (var session in sessions)
            {
                try
                {
                    await session.CompleteAsync(CancellationToken.None);
                }
                catch
                {
                    // The committed marker deliberately remains on disk. Startup recovery will
                    // keep the verified target state and retry journal cleanup.
                    cleanupFailed = true;
                }
            }

            if (!cleanupFailed)
            {
                await RestoreTransactionJournal.CompleteAsync(jobId, CancellationToken.None);
            }

            await reportProgress(JobStatus.Succeeded, "恢复完成并通过逐项验证", CancellationToken.None);
            return new RestoreExecutionResult(JobStatus.Succeeded, "恢复完成", []);
        }
        // 事务会话创建后，取消同样可能发生在停止、写入、启动或回读中途。
        // 此时绝不能直接释放会话；必须使用不可取消令牌完成全量回滚。
        catch (Exception exception)
        {
            if (durablyCommitted)
            {
                // Application state was fully verified and durably committed. A later status
                // reporting or cleanup error must never turn a successful restore into a partial
                // rollback. Remaining journals are finalized on the next Agent start.
                return new RestoreExecutionResult(JobStatus.Succeeded, "恢复完成", []);
            }

            var rollbackFailures = new List<string>();
            for (var index = sessions.Count - 1; index >= 0; index--)
            {
                try
                {
                    await sessions[index].RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add($"{sessions[index].Kind}: {rollbackException.Message}");
                }
            }

            if (rollbackFailures.Count > 0)
            {
                await reportProgress(JobStatus.RollbackFailed, "恢复失败且事务回滚未完成", CancellationToken.None);
                return new RestoreExecutionResult(
                    JobStatus.RollbackFailed,
                    CreateFailureMessage(failureContext, exception),
                    rollbackFailures);
            }

            await RestoreTransactionJournal.CompleteAsync(jobId, CancellationToken.None);

            await reportProgress(JobStatus.FailedRolledBack, "恢复失败，已还原目标电脑原状态", CancellationToken.None);
            var contextualMessage = CreateFailureMessage(failureContext, exception);
            var failureMessage = verificationDifferences.Count == 0
                ? contextualMessage
                : $"{contextualMessage}：{string.Join("；", verificationDifferences.Take(3))}"
                  + (verificationDifferences.Count > 3
                      ? $"；另有 {verificationDifferences.Count - 3} 项差异"
                      : string.Empty);
            return new RestoreExecutionResult(JobStatus.FailedRolledBack, failureMessage, verificationDifferences);
        }
        finally
        {
            for (var index = sessions.Count - 1; index >= 0; index--)
            {
                await sessions[index].DisposeAsync();
            }

            await DisposeRuntimeLeasesAsync(runtimeLeases);
        }
    }

    private static async Task DisposeRuntimeLeasesAsync(List<IApplicationRuntimeLease> runtimeLeases)
    {
        for (var index = runtimeLeases.Count - 1; index >= 0; index--)
        {
            await runtimeLeases[index].DisposeAsync();
        }

        runtimeLeases.Clear();
    }

    public void Dispose()
    {
        if (_ownsOperationGate)
        {
            _operationGate.Dispose();
        }
    }

    private static string DisplayName(ApplicationKind kind) => kind switch
    {
        ApplicationKind.Obs => "OBS",
        ApplicationKind.LiveCompanion => "抖音直播伴侣",
        _ => kind.ToString()
    };

    private static string CreateFailureMessage(string context, Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.StartsWith(context, StringComparison.Ordinal)
            ? message
            : $"{context}失败：{message}";
    }
}
