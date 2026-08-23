using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;
using LiveStudio.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class AgentWorker(
    DeviceApiClient apiClient,
    LocalSnapshotIndex snapshotIndex,
    SnapshotCaptureService captureService,
    LocalRestoreService restoreService,
    CurrentStatePublisher currentStatePublisher,
    IEnumerable<IApplicationAdapter> applicationAdapters,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogHeartbeatFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1001, nameof(LogHeartbeatFailure)),
        "Agent 心跳上报失败");
    private static readonly Action<ILogger, Guid, Exception?> LogJobFailure = LoggerMessage.Define<Guid>(
        LogLevel.Error,
        new EventId(1002, nameof(LogJobFailure)),
        "处理任务 {JobId} 失败");
    private static readonly Action<ILogger, Exception?> LogConnectionFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1003, nameof(LogConnectionFailure)),
        "Agent 云端实时连接中断，稍后重试");
    private readonly Channel<AgentJobNotification> jobs = Channel.CreateUnbounded<AgentJobNotification>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly HashSet<Guid> scheduledJobs = [];
    private readonly Lock scheduleLock = new();
    private readonly IReadOnlyList<IApplicationAdapter> adapters = applicationAdapters.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await snapshotIndex.InitializeAsync(stoppingToken);
        var connection = RunConnectionLoopAsync(stoppingToken);
        var heartbeat = RunHeartbeatLoopAsync(stoppingToken);
        var processing = ProcessJobsAsync(stoppingToken);
        await Task.WhenAll(connection, heartbeat, processing);
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = BuildHubConnection();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.On<AgentJobNotification>("JobAvailable", Schedule);
            connection.Reconnected += async _ => await ScheduleAvailableAsync(cancellationToken);
            connection.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            try
            {
                await connection.StartAsync(cancellationToken);
                await ScheduleAvailableAsync(cancellationToken);
                await FlushJobEventsAsync(cancellationToken);
                await closed.Task.WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogConnectionFailure(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private HubConnection BuildHubConnection()
    {
        var hubUri = new Uri(apiClient.Credentials.ServiceUri, "/hubs/agents");
        var authorization = apiClient.CreateAuthorizationHeader().ToString();
        return new HubConnectionBuilder()
            .WithUrl(hubUri, options => options.Headers["Authorization"] = authorization)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                var telemetry = await CaptureTelemetryAsync(cancellationToken);
                await apiClient.SendHeartbeatAsync(telemetry.Heartbeat, cancellationToken);
                if (telemetry.CurrentState is not null)
                {
                    await apiClient.UpdateCurrentStateAsync(
                        telemetry.CurrentState,
                        [],
                        CurrentStateReason.Heartbeat,
                        cancellationToken);
                }

                await ScheduleAvailableAsync(cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                LogHeartbeatFailure(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ScheduleAvailableAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await apiClient.GetAvailableJobsAsync(cancellationToken))
        {
            Schedule(job);
        }
    }

    private void Schedule(AgentJobNotification job)
    {
        lock (scheduleLock)
        {
            if (!scheduledJobs.Add(job.JobId))
            {
                return;
            }
        }

        jobs.Writer.TryWrite(job);
    }

    private async Task ProcessJobsAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in jobs.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var job = await apiClient.ClaimAsync(notification.JobId, cancellationToken);
                if (job is null)
                {
                    continue;
                }

                await ExecuteJobAsync(job, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogJobFailure(logger, notification.JobId, exception);
            }
            finally
            {
                lock (scheduleLock)
                {
                    scheduledJobs.Remove(notification.JobId);
                }
            }
        }
    }

    private async Task ExecuteJobAsync(ClaimJobResponse job, CancellationToken cancellationToken)
    {
        var lastStatus = JobStatus.Claimed;
        var sequence = 1L;
        async Task ReportAsync(
            JobStatus status,
            string message,
            string? detailCode = null,
            string? verificationDetail = null)
        {
            if (status == lastStatus)
            {
                return;
            }

            sequence++;
            await snapshotIndex.EnqueueJobEventAsync(
                new PendingJobEvent(
                    Guid.NewGuid(),
                    job.Id,
                    job.ExecutionId,
                    sequence,
                    status,
                    message,
                    detailCode,
                    verificationDetail,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            lastStatus = status;
            await TryFlushJobEventsAsync(cancellationToken);
        }

        try
        {
            switch (job.Kind)
            {
                case JobKind.Capture:
                    await ReportAsync(JobStatus.Preflight, "正在检查 OBS、直播伴侣和直播状态");
                    await ReportAsync(JobStatus.Capturing, "正在读取设备、视频格式、滤镜和预览图");
                    var snapshot = await captureService.CaptureAsync(job.Name, cancellationToken);
                    await ReportAsync(JobStatus.Packaging, "联合存档已写入本机并完成签名校验");
                    await ReportAsync(JobStatus.Uploading, "正在上传联合存档和预览图");
                    await apiClient.UploadSnapshotAsync(snapshot, cancellationToken);
                    await snapshotIndex.MarkUploadedAsync(snapshot.Id, cancellationToken);
                    await ReportAsync(JobStatus.Succeeded, "联合存档已保存并同步到云端");
                    break;

                case JobKind.Restore:
                    if (job.SnapshotId is not { } snapshotId)
                    {
                        await ReportAsync(JobStatus.Preflight, "正在检查恢复任务参数");
                        await ReportAsync(JobStatus.IncompatibleVersion, "恢复任务缺少存档 ID", "SnapshotIdRequired");
                        return;
                    }

                    await ReportAsync(JobStatus.Preflight, "正在下载并验证目标存档");
                    var download = await apiClient.DownloadSnapshotAsync(snapshotId, cancellationToken);
                    var mappings = await apiClient.GetMappingsAsync(cancellationToken);
                    var result = await restoreService.RestoreDownloadedAsync(
                        download,
                        mappings,
                        async (status, message, _) => await ReportAsync(status, message),
                        cancellationToken);
                    if (result.IsSuccess)
                    {
                        try
                        {
                            await currentStatePublisher.PublishAsync(CurrentStateReason.Restore, cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            LogHeartbeatFailure(logger, exception);
                        }
                    }

                    if (result.Status != lastStatus)
                    {
                        await ReportAsync(
                            result.Status,
                            result.Message,
                            result.Status.ToString(),
                            result.Differences.Count == 0
                                ? null
                                : string.Join(Environment.NewLine, result.Differences));
                    }

                    break;

                case JobKind.RefreshPreview:
                    await ReportAsync(JobStatus.Preflight, "正在检查应用连接和当前参数");
                    await ReportAsync(JobStatus.RefreshingPreview, "正在回读参数并生成当前画面预览");
                    await currentStatePublisher.PublishAsync(CurrentStateReason.RemoteRefresh, cancellationToken);
                    await ReportAsync(JobStatus.Succeeded, "当前参数和画面预览已刷新");
                    break;

                default:
                    await ReportAsync(JobStatus.Preflight, "正在检查任务类型");
                    await ReportAsync(JobStatus.IncompatibleVersion, $"不支持任务类型 {job.Kind}", "UnsupportedJobKind");
                    break;
            }
        }
        catch (SnapshotCaptureException exception)
        {
            var status = exception.Message.Contains("直播", StringComparison.Ordinal)
                || exception.Message.Contains("推流", StringComparison.Ordinal)
                || exception.Message.Contains("录制", StringComparison.Ordinal)
                ? JobStatus.BlockedByLiveSession
                : JobStatus.IncompatibleVersion;
            await ReportAsync(status, exception.Message, exception.GetType().Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var terminalStatus = lastStatus is JobStatus.BackingUp
                or JobStatus.Capturing
                or JobStatus.Packaging
                or JobStatus.Uploading
                or JobStatus.StoppingApplications
                or JobStatus.Applying
                or JobStatus.StartingApplications
                or JobStatus.Verifying
                ? JobStatus.FailedRolledBack
                : JobStatus.IncompatibleVersion;
            await ReportAsync(terminalStatus, exception.Message, exception.GetType().Name);
        }
    }

    private async Task TryFlushJobEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushJobEventsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            LogConnectionFailure(logger, exception);
        }
    }

    private async Task FlushJobEventsAsync(CancellationToken cancellationToken)
    {
        foreach (var jobEvent in await snapshotIndex.GetPendingJobEventsAsync(cancellationToken))
        {
            await apiClient.ReportAsync(jobEvent, cancellationToken);
            await snapshotIndex.MarkJobEventUploadedAsync(jobEvent.Id, cancellationToken);
        }
    }

    private async Task<(HeartbeatRequest Heartbeat, CurrentParameterState? CurrentState)> CaptureTelemetryAsync(
        CancellationToken cancellationToken)
    {
        var applications = new List<ApplicationSnapshot>();
        foreach (var adapter in adapters)
        {
            try
            {
                var status = await adapter.InspectAsync(cancellationToken);
                if (status.IsRunning || adapter.Kind == ApplicationKind.LiveCompanion)
                {
                    applications.Add(await adapter.CaptureAsync(cancellationToken));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogHeartbeatFailure(logger, exception);
            }
        }

        var videoSources = applications
            .SelectMany(application => application.Sources
                .Where(source => source.Device is not null)
                .Select(source => new DeviceVideoSourceCapability(
                    application.Kind,
                    source.LogicalId,
                    source.Name,
                    source.Device! with
                    {
                        SupportedModes = source.Mode is null ? [] : [source.Mode]
                    },
                    source.Mode)))
            .ToArray();
        var captureDevices = videoSources
            .GroupBy(source => source.Device.InterfaceHint ?? source.Device.FriendlyName, StringComparer.Ordinal)
            .Select(group =>
            {
                var device = group.First().Device;
                return device with
                {
                    SupportedModes = group
                        .Where(source => source.CurrentMode is not null)
                        .Select(source => source.CurrentMode!)
                        .Distinct()
                        .ToArray()
                };
            })
            .ToArray();
        var availableFilters = new Dictionary<ApplicationKind, IReadOnlyList<string>>
        {
            [ApplicationKind.Obs] = ObsConnectionOptions.BuiltInVideoFilterKinds
                .Concat(applications
                    .Where(application => application.Kind == ApplicationKind.Obs)
                    .SelectMany(application => application.Sources)
                    .SelectMany(source => source.Filters)
                    .Select(filter => filter.Kind))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            [ApplicationKind.LiveCompanion] = applications
                .Where(application => application.Kind == ApplicationKind.LiveCompanion)
                .SelectMany(application => application.Sources)
                .SelectMany(source => source.Filters)
                .Select(filter => filter.Kind)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
        var capability = new DeviceCapability(
            apiClient.Credentials.DeviceId,
            DateTimeOffset.UtcNow,
            captureDevices,
            availableFilters,
            videoSources);
        CurrentParameterState? currentState = null;
        if (applications.Count > 0)
        {
            var ordered = applications.OrderBy(application => application.Kind).ToArray();
            var content = JsonSerializer.SerializeToUtf8Bytes(ordered);
            currentState = new CurrentParameterState(
                apiClient.Credentials.DeviceId,
                apiClient.Credentials.RoomId,
                DateTimeOffset.UtcNow,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                ordered);
        }

        return (
            new HeartbeatRequest(
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
                Environment.OSVersion.VersionString,
                Environment.UserInteractive,
                ReadApplicationVersions(),
                capability),
            currentState);
    }

    private static Dictionary<ApplicationKind, string> ReadApplicationVersions()
    {
        var versions = new Dictionary<ApplicationKind, string>();
        TryReadProcessVersion("obs64", ApplicationKind.Obs, versions);
        foreach (var processName in new[]
                 {
                     "StreamingTool", "直播伴侣", "douyin-live-companion", "douyin_live_companion", "LiveCompanion"
                 })
        {
            if (TryReadProcessVersion(processName, ApplicationKind.LiveCompanion, versions))
            {
                break;
            }
        }

        return versions;
    }

    private static bool TryReadProcessVersion(
        string processName,
        ApplicationKind application,
        Dictionary<ApplicationKind, string> destination)
    {
        using var process = Process.GetProcessesByName(processName).FirstOrDefault();
        try
        {
            var version = process?.MainModule?.FileVersionInfo.FileVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            destination[application] = version;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
