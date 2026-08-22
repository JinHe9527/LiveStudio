using LiveStudio.Contracts;

namespace LiveStudio.Core;

public sealed class RestoreCoordinator(IEnumerable<IApplicationAdapter> adapters)
{
    private readonly Dictionary<ApplicationKind, IApplicationAdapter> _adapters = adapters
        .ToDictionary(adapter => adapter.Kind);

    public async Task<RestoreExecutionResult> ExecuteAsync(
        Guid jobId,
        CombinedSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        bool isUnattended,
        string assetDirectory,
        Func<CancellationToken, Task> prepareAssets,
        Func<JobStatus, string, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDirectory);
        ArgumentNullException.ThrowIfNull(prepareAssets);
        ArgumentNullException.ThrowIfNull(reportProgress);

        await reportProgress(JobStatus.Preflight, "正在检查直播状态、设备映射和版本兼容性", cancellationToken);

        var contexts = new List<(IApplicationAdapter Adapter, RestoreExecutionContext Context)>();
        foreach (var application in snapshot.Applications)
        {
            if (!_adapters.TryGetValue(application.Kind, out var adapter))
            {
                return new RestoreExecutionResult(
                    JobStatus.IncompatibleVersion,
                    $"没有可用的 {application.Kind} 适配器",
                    []);
            }

            var context = new RestoreExecutionContext(
                jobId,
                application,
                mappings.Where(mapping => mapping.Application == application.Kind).ToArray(),
                isUnattended,
                assetDirectory);
            var status = await adapter.InspectAsync(cancellationToken);
            if ((status.IsStreaming || status.IsRecording) && status.CanDetermineLiveState)
            {
                return new RestoreExecutionResult(
                    JobStatus.BlockedByLiveSession,
                    $"{application.Kind} 正在推流或录制",
                    []);
            }

            if (isUnattended && !status.CanDetermineLiveState)
            {
                return new RestoreExecutionResult(
                    JobStatus.IncompatibleVersion,
                    $"无法确认 {application.Kind} 是否正在直播，禁止无人值守恢复",
                    []);
            }

            var preflight = await adapter.PreflightAsync(context, cancellationToken);
            if (!preflight.CanProceed)
            {
                return new RestoreExecutionResult(preflight.FailureStatus, preflight.Message, []);
            }

            contexts.Add((adapter, context));
        }

        var sessions = new List<IApplicationRestoreSession>();
        IReadOnlyList<string> verificationDifferences = [];
        try
        {
            await reportProgress(JobStatus.BackingUp, "正在创建目标电脑事务快照", cancellationToken);
            foreach (var item in contexts)
            {
                sessions.Add(await item.Adapter.BeginRestoreAsync(item.Context, cancellationToken));
            }

            await reportProgress(JobStatus.StoppingApplications, "正在停止应用并等待配置落盘", cancellationToken);
            foreach (var session in sessions)
            {
                await session.StopAsync(cancellationToken);
            }

            await reportProgress(JobStatus.Applying, "正在应用设备、画面格式和视频滤镜", cancellationToken);
            await prepareAssets(cancellationToken);
            foreach (var session in sessions)
            {
                await session.ApplyAsync(cancellationToken);
            }

            await reportProgress(JobStatus.StartingApplications, "正在恢复应用运行状态", cancellationToken);
            foreach (var session in sessions)
            {
                await session.StartAsync(cancellationToken);
            }

            await reportProgress(JobStatus.Verifying, "正在逐项回读恢复结果", cancellationToken);
            var differences = new List<string>();
            foreach (var session in sessions)
            {
                var verification = await session.VerifyAsync(cancellationToken);
                if (!verification.IsMatch)
                {
                    differences.AddRange(verification.Differences.Select(value => $"{session.Kind}: {value}"));
                }
            }

            if (differences.Count > 0)
            {
                verificationDifferences = differences;
                throw new InvalidOperationException("恢复后的参数与存档不一致");
            }

            foreach (var session in sessions)
            {
                await session.CommitAsync(cancellationToken);
            }

            await reportProgress(JobStatus.Succeeded, "恢复完成并通过逐项验证", cancellationToken);
            return new RestoreExecutionResult(JobStatus.Succeeded, "恢复完成", []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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
                return new RestoreExecutionResult(JobStatus.RollbackFailed, exception.Message, rollbackFailures);
            }

            await reportProgress(JobStatus.FailedRolledBack, "恢复失败，已还原目标电脑原状态", CancellationToken.None);
            return new RestoreExecutionResult(JobStatus.FailedRolledBack, exception.Message, verificationDifferences);
        }
        finally
        {
            for (var index = sessions.Count - 1; index >= 0; index--)
            {
                await sessions[index].DisposeAsync();
            }
        }
    }
}
