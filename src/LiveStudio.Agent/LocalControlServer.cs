using System.IO.Pipes;
using LiveStudio.Contracts;
using LiveStudio.Core;
using LiveStudio.Adapters.Obs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class LocalControlServer(
    IDeviceCredentialStore credentialStore,
    IEnumerable<IApplicationAdapter> applicationAdapters,
    LocalSnapshotIndex snapshotIndex,
    SnapshotCaptureService captureService,
    LocalRestoreService restoreService,
    SnapshotTransferService transferService,
    AgentObsConfigurationStore obsConfiguration,
    LanSnapshotConfigurationStore lanConfiguration,
    LanSnapshotWorker lanSnapshotWorker,
    IObsDeviceCatalog obsDeviceCatalog,
    CloudAgentRuntime cloudRuntime,
    ILogger<LocalControlServer> logger) : BackgroundService
{
    private const int ListenerCount = 4;
    private static readonly Action<ILogger, Exception?> LogConnectionInterrupted = LoggerMessage.Define(
        LogLevel.Debug,
        new EventId(1201, nameof(LogConnectionInterrupted)),
        "本机控制连接已中断");
    private static readonly Action<ILogger, Exception?> LogListenerFailure = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1202, nameof(LogListenerFailure)),
        "处理本机控制连接失败");
    private static readonly Action<ILogger, LocalControlMethod, Exception?> LogRequestFailure =
        LoggerMessage.Define<LocalControlMethod>(
            LogLevel.Error,
            new EventId(1203, nameof(LogRequestFailure)),
            "本机控制请求 {Method} 执行失败");
    private readonly Dictionary<ApplicationKind, IApplicationAdapter> adapters = applicationAdapters
        .ToDictionary(adapter => adapter.Kind);
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly Lock stateLock = new();
    private string operationMessage = "本机执行端已就绪";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await snapshotIndex.InitializeAsync(stoppingToken);
        var listeners = Enumerable.Range(0, ListenerCount)
            .Select(_ => RunListenerAsync(stoppingToken));
        await Task.WhenAll(listeners);
    }

    public override void Dispose()
    {
        operationLock.Dispose();
        base.Dispose();
    }

    private async Task RunListenerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                LocalControlProtocol.PipeName,
                PipeDirection.InOut,
                ListenerCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                await ProcessConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException exception)
            {
                LogConnectionInterrupted(logger, exception);
            }
            catch (Exception exception)
            {
                LogListenerFailure(logger, exception);
            }
        }
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var request = await LocalControlProtocol.ReadAsync<LocalControlRequest>(stream, cancellationToken);
        LocalControlResponse response;
        try
        {
            response = request.Method switch
            {
                LocalControlMethod.GetState => LocalControlProtocol.CreateSuccess(
                    request.RequestId,
                    await GetStateAsync(cancellationToken)),
                LocalControlMethod.RefreshCurrentState => await RefreshCurrentStateAsync(request, cancellationToken),
                LocalControlMethod.CaptureSnapshot => await CaptureAsync(request, cancellationToken),
                LocalControlMethod.RestoreSnapshot => await RestoreAsync(request, cancellationToken),
                LocalControlMethod.ConfigureObs => await ConfigureObsAsync(request, cancellationToken),
                LocalControlMethod.ConfigureLanDirectory => await ConfigureLanDirectoryAsync(request, cancellationToken),
                LocalControlMethod.ConfigureAutoStart => await ConfigureAutoStartAsync(request, cancellationToken),
                LocalControlMethod.EnrollDevice => await EnrollDeviceAsync(request, cancellationToken),
                LocalControlMethod.GetMappingContext => await GetMappingContextAsync(request, cancellationToken),
                LocalControlMethod.GetSnapshotDetail => await GetSnapshotDetailAsync(request, cancellationToken),
                LocalControlMethod.SaveDeviceMapping => await SaveDeviceMappingAsync(request, cancellationToken),
                LocalControlMethod.InspectSnapshotFile => await InspectSnapshotFileAsync(request, cancellationToken),
                LocalControlMethod.ImportSnapshotFile => await ImportSnapshotFileAsync(request, cancellationToken),
                LocalControlMethod.ExportSnapshotFile => await ExportSnapshotFileAsync(request, cancellationToken),
                _ => LocalControlProtocol.CreateFailure(
                    request.RequestId,
                    "UnsupportedMethod",
                    $"本机执行端不支持 {request.Method}")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRequestFailure(logger, request.Method, exception);
            response = LocalControlProtocol.CreateFailure(
                request.RequestId,
                exception.GetType().Name,
                exception.Message);
        }

        await LocalControlProtocol.WriteAsync(stream, response, cancellationToken);
    }

    private async Task<LocalAgentState> GetStateAsync(CancellationToken cancellationToken)
    {
        var applicationStates = new List<LocalApplicationState>();
        foreach (var kind in Enum.GetValues<ApplicationKind>())
        {
            if (!adapters.TryGetValue(kind, out var adapter))
            {
                applicationStates.Add(new LocalApplicationState(
                    kind,
                    false,
                    false,
                    false,
                    false,
                    false,
                    "unknown",
                    "没有可用的适配器"));
                continue;
            }

            try
            {
                var status = await adapter.InspectAsync(cancellationToken);
                applicationStates.Add(new LocalApplicationState(
                    kind,
                    true,
                    status.IsRunning,
                    status.IsStreaming,
                    status.IsRecording,
                    status.CanDetermineLiveState,
                    status.Version,
                    CreateApplicationStatusMessage(status)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                applicationStates.Add(new LocalApplicationState(
                    kind,
                    true,
                    false,
                    false,
                    false,
                    false,
                    "unknown",
                    exception.Message));
            }
        }

        var snapshots = await snapshotIndex.GetAllAsync(cancellationToken);
        var operations = await snapshotIndex.GetOperationsAsync(cancellationToken);
        var isBusy = operationLock.CurrentCount == 0;
        var hasLocalIdentity = credentialStore.TryLoad(out var credentials);
        var isEnrolled = credentials?.IsCloudEnrolled == true;
        var adaptersReady = applicationStates.All(state => state.AdapterAvailable);
        var applicationsSafe = applicationStates.All(state =>
            state.CanDetermineLiveState && !state.IsStreaming && !state.IsRecording);
        var canCapture = hasLocalIdentity && adaptersReady && applicationsSafe && !isBusy;
        var statusMessage = GetOperationMessage();
        if (!hasLocalIdentity)
        {
            statusMessage = "本机存档身份尚未初始化";
        }
        else if (!adaptersReady)
        {
            statusMessage = "OBS 或直播伴侣适配器尚未就绪";
        }
        else if (!applicationsSafe)
        {
            statusMessage = "应用未连接、正在直播，或无法可靠读取直播状态";
        }

        return new LocalAgentState(
            Environment.MachineName,
            isEnrolled,
            canCapture,
            canCapture && snapshots.Count > 0,
            isBusy,
            await WindowsStartupRegistration.IsEnabledAsync(),
            statusMessage,
            lanConfiguration.SharedDirectory,
            lanSnapshotWorker.Status,
            applicationStates,
            snapshots.Select(snapshot => new LocalSnapshotSummary(
                snapshot.Id,
                snapshot.Name,
                snapshot.CreatedAt,
                snapshot.Length,
                snapshot.Uploaded,
                snapshot.UploadEligible)).ToArray(),
            operations.Select(operation => new LocalOperationSummary(
                operation.Id,
                operation.Kind,
                operation.Status,
                operation.Message,
                operation.SnapshotId,
                operation.StartedAt,
                operation.CompletedAt)).ToArray());
    }

    private async Task<LocalControlResponse> CaptureAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var capture = LocalControlProtocol.DeserializePayload<CaptureLocalSnapshotRequest>(request.Payload);
        if (string.IsNullOrWhiteSpace(capture.Name) || capture.Name.Trim().Length > 120)
        {
            return LocalControlProtocol.CreateFailure(
                request.RequestId,
                "InvalidSnapshotName",
                "存档名称不能为空且不能超过 120 个字符");
        }

        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return LocalControlProtocol.CreateFailure(request.RequestId, "AgentBusy", "本机执行端正在执行其他任务");
        }

        try
        {
            var operation = new LocalOperationRecord(
                Guid.NewGuid(),
                LocalOperationKind.Capture,
                LocalOperationStatus.Running,
                "正在联合保存 OBS 与直播伴侣参数",
                null,
                DateTimeOffset.UtcNow,
                null);
            await snapshotIndex.SaveOperationAsync(operation, cancellationToken);
            SetOperationMessage("正在联合保存 OBS 与直播伴侣参数");
            try
            {
                var snapshot = await captureService.CaptureAsync(capture.Name.Trim(), cancellationToken);
                var completedAt = DateTimeOffset.UtcNow;
                await snapshotIndex.SaveOperationAsync(
                    operation with
                    {
                        Status = LocalOperationStatus.Succeeded,
                        Message = "联合存档保存完成",
                        SnapshotId = snapshot.Id,
                        CompletedAt = completedAt
                    },
                    cancellationToken);
                SetOperationMessage("联合存档保存完成");
                return LocalControlProtocol.CreateSuccess(
                    request.RequestId,
                    new LocalSnapshotOperationResult(snapshot.Id, snapshot.Name, completedAt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await snapshotIndex.SaveOperationAsync(
                    operation with
                    {
                        Status = LocalOperationStatus.Failed,
                        Message = exception.Message,
                        CompletedAt = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<LocalControlResponse> RestoreAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var restore = LocalControlProtocol.DeserializePayload<RestoreLocalSnapshotRequest>(request.Payload);
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return LocalControlProtocol.CreateFailure(request.RequestId, "AgentBusy", "本机执行端正在执行其他任务");
        }

        try
        {
            var operation = new LocalOperationRecord(
                Guid.NewGuid(),
                LocalOperationKind.Restore,
                LocalOperationStatus.Running,
                "正在预检并应用存档",
                restore.SnapshotId,
                DateTimeOffset.UtcNow,
                null);
            await snapshotIndex.SaveOperationAsync(operation, cancellationToken);
            try
            {
                var result = await restoreService.RestoreAsync(
                    restore.SnapshotId,
                    (status, message, _) =>
                    {
                        SetOperationMessage(message);
                        return Task.CompletedTask;
                    },
                    cancellationToken);
                var operationStatus = result.Status switch
                {
                    JobStatus.Succeeded => LocalOperationStatus.Succeeded,
                    JobStatus.FailedRolledBack => LocalOperationStatus.FailedRolledBack,
                    JobStatus.RollbackFailed => LocalOperationStatus.RollbackFailed,
                    _ => LocalOperationStatus.Blocked
                };
                await snapshotIndex.SaveOperationAsync(
                    operation with
                    {
                        Status = operationStatus,
                        Message = result.Message,
                        CompletedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
                if (!result.IsSuccess)
                {
                    return LocalControlProtocol.CreateFailure(
                        request.RequestId,
                        result.Status.ToString(),
                        result.Message);
                }

                var snapshot = await snapshotIndex.FindAsync(restore.SnapshotId, cancellationToken)
                    ?? throw new FileNotFoundException($"找不到本地存档 {restore.SnapshotId}");
                try
                {
                    if (!await cloudRuntime.PublishCurrentStateAsync(CurrentStateReason.Restore, cancellationToken))
                    {
                        SetOperationMessage("恢复已完成，Agent 尚未连接云端");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogRequestFailure(logger, LocalControlMethod.RestoreSnapshot, exception);
                    SetOperationMessage("恢复已完成，但当前画面预览上传失败");
                }

                return LocalControlProtocol.CreateSuccess(
                    request.RequestId,
                    new LocalSnapshotOperationResult(snapshot.Id, snapshot.Name, DateTimeOffset.UtcNow));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await snapshotIndex.SaveOperationAsync(
                    operation with
                    {
                        Status = LocalOperationStatus.Failed,
                        Message = exception.Message,
                        CompletedAt = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<LocalControlResponse> ConfigureObsAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = LocalControlProtocol.DeserializePayload<ConfigureObsRequest>(request.Payload);
        await obsConfiguration.SaveAsync(configuration, cancellationToken);
        SetOperationMessage("OBS WebSocket 连接设置已保存");
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await GetStateAsync(cancellationToken));
    }

    private async Task<LocalControlResponse> RefreshCurrentStateAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        if (await cloudRuntime.PublishCurrentStateAsync(CurrentStateReason.ManualRefresh, cancellationToken))
        {
            SetOperationMessage("当前参数和画面预览已刷新到云端");
        }

        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await GetStateAsync(cancellationToken));
    }

    private async Task<LocalControlResponse> ConfigureLanDirectoryAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = LocalControlProtocol.DeserializePayload<ConfigureLanDirectoryRequest>(request.Payload);
        await lanConfiguration.SaveAsync(configuration, cancellationToken);
        SetOperationMessage(string.IsNullOrWhiteSpace(configuration.Path)
            ? "已停用局域网存档同步"
            : "局域网存档目录已保存");
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await GetStateAsync(cancellationToken));
    }

    private async Task<LocalControlResponse> ConfigureAutoStartAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = LocalControlProtocol.DeserializePayload<ConfigureAutoStartRequest>(request.Payload);
        await WindowsStartupRegistration.SetEnabledAsync(configuration.Enabled);
        SetOperationMessage(configuration.Enabled
            ? "Agent 已设为登录 Windows 后自动启动"
            : "Agent 已取消自动启动");
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await GetStateAsync(cancellationToken));
    }

    private async Task<LocalControlResponse> EnrollDeviceAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var enrollment = LocalControlProtocol.DeserializePayload<EnrollLocalDeviceRequest>(request.Payload);
        if (!enrollment.ServiceUri.IsAbsoluteUri
            || enrollment.ServiceUri.Scheme != Uri.UriSchemeHttps
                && !(enrollment.ServiceUri.Scheme == Uri.UriSchemeHttp && enrollment.ServiceUri.IsLoopback)
            || enrollment.EnrollmentToken.Length is < 32 or > 256
            || string.IsNullOrWhiteSpace(enrollment.DeviceName)
            || enrollment.DeviceName.Trim().Length > 120)
        {
            return LocalControlProtocol.CreateFailure(
                request.RequestId,
                "InvalidEnrollment",
                "设备注册信息无效，远程云服务必须使用 HTTPS");
        }

        using var client = new HttpClient { BaseAddress = enrollment.ServiceUri };
        var enrollmentClient = new DeviceEnrollmentClient(client, credentialStore);
        await enrollmentClient.EnrollAsync(
            enrollment.EnrollmentToken,
            enrollment.DeviceName.Trim(),
            cancellationToken);
        SetOperationMessage("设备注册完成，正在连接云端");
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await GetStateAsync(cancellationToken));
    }

    private async Task<LocalControlResponse> InspectSnapshotFileAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var file = LocalControlProtocol.DeserializePayload<SnapshotFileRequest>(request.Payload);
        var preview = await transferService.InspectAsync(file.Path, cancellationToken);
        return LocalControlProtocol.CreateSuccess(request.RequestId, preview);
    }

    private async Task<LocalControlResponse> GetMappingContextAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var query = LocalControlProtocol.DeserializePayload<GetLocalMappingContextRequest>(request.Payload);
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await CreateMappingContextAsync(query.SnapshotId, cancellationToken));
    }

    private async Task<LocalControlResponse> GetSnapshotDetailAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var query = LocalControlProtocol.DeserializePayload<GetLocalSnapshotDetailRequest>(request.Payload);
        var package = await transferService.ReadLocalAsync(query.SnapshotId, cancellationToken);
        return LocalControlProtocol.CreateSuccess(request.RequestId, package.Snapshot);
    }

    private async Task<LocalControlResponse> SaveDeviceMappingAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var save = LocalControlProtocol.DeserializePayload<SaveLocalDeviceMappingRequest>(request.Payload);
        if (string.IsNullOrWhiteSpace(save.TargetDeviceId)
            || string.IsNullOrWhiteSpace(save.TargetSourceName)
            || save.TargetDeviceId.Length > 1024
            || save.TargetSourceName.Length > 256)
        {
            return LocalControlProtocol.CreateFailure(
                request.RequestId,
                "InvalidDeviceMapping",
                "目标设备和 OBS 来源不能为空");
        }

        var credentials = credentialStore.Load();
        var package = await transferService.ReadLocalAsync(save.SnapshotId, cancellationToken);
        var source = package.Snapshot.Applications
            .Where(application => application.Kind == save.Application)
            .SelectMany(application => application.Sources)
            .SingleOrDefault(candidate => candidate.LogicalId == save.SourceLogicalId)
            ?? throw new InvalidDataException("存档中找不到要映射的来源");
        var targets = await CaptureMappingTargetsAsync(cancellationToken);
        var target = targets.SingleOrDefault(candidate =>
            candidate.Application == save.Application
            && string.Equals(candidate.SourceName, save.TargetSourceName.Trim(), StringComparison.Ordinal)
            && string.Equals(candidate.TargetDeviceId, save.TargetDeviceId.Trim(), StringComparison.Ordinal))
            ?? throw new InvalidDataException("目标来源不是 Agent 当前回读到的采集来源");
        if (source.Mode is { } mode
            && save.Application == ApplicationKind.Obs
            && !await obsDeviceCatalog.SupportsModeAsync(
                target.TargetDeviceId,
                target.SourceName,
                mode,
                cancellationToken))
        {
            return LocalControlProtocol.CreateFailure(
                request.RequestId,
                "UnsupportedDeviceMode",
                $"目标来源不支持 {FormatMode(mode)}");
        }

        var existing = (await snapshotIndex.GetMappingsAsync(credentials.DeviceId, cancellationToken))
            .SingleOrDefault(mapping => mapping.SourceLogicalId == source.LogicalId
                && mapping.Application == save.Application);
        await snapshotIndex.SaveMappingAsync(
            new DeviceMapping(
                existing?.Id ?? Guid.NewGuid(),
                credentials.OrganizationId,
                credentials.DeviceId,
                source.LogicalId,
                save.Application,
                target.TargetDeviceId,
                target.SourceName,
                string.Empty,
                false),
            cancellationToken);
        SetOperationMessage($"已映射来源“{source.Name}”");
        return LocalControlProtocol.CreateSuccess(
            request.RequestId,
            await CreateMappingContextAsync(save.SnapshotId, cancellationToken));
    }

    private async Task<LocalMappingContext> CreateMappingContextAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var credentials = credentialStore.Load();
        var package = await transferService.ReadLocalAsync(snapshotId, cancellationToken);
        var mappings = await snapshotIndex.GetMappingsAsync(credentials.DeviceId, cancellationToken);
        var mappingBySource = mappings.ToDictionary(
            mapping => (mapping.Application, mapping.SourceLogicalId));
        var sources = package.Snapshot.Applications
            .SelectMany(application => application.Sources.Select(source => new LocalMappingSource(
                source.LogicalId,
                application.Kind,
                source.Name,
                source.Device?.FriendlyName ?? "未识别设备",
                source.Mode,
                mappingBySource.GetValueOrDefault((application.Kind, source.LogicalId)))))
            .OrderBy(source => source.Application)
            .ThenBy(source => source.SourceName, StringComparer.Ordinal)
            .ToArray();
        return new LocalMappingContext(
            snapshotId,
            sources,
            await CaptureMappingTargetsAsync(cancellationToken));
    }

    private async Task<IReadOnlyList<LocalMappingTarget>> CaptureMappingTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new List<LocalMappingTarget>();
        foreach (var adapter in adapters.Values)
        {
            try
            {
                var snapshot = await adapter.CaptureAsync(cancellationToken);
                targets.AddRange(snapshot.Sources
                    .Where(source => !string.IsNullOrWhiteSpace(source.Device?.InterfaceHint))
                    .Select(source => new LocalMappingTarget(
                        adapter.Kind,
                        source.Name,
                        source.Device!.InterfaceHint!,
                        source.Device.FriendlyName,
                        source.Mode)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRequestFailure(logger, LocalControlMethod.GetMappingContext, exception);
            }
        }

        return targets
            .OrderBy(target => target.Application)
            .ThenBy(target => target.SourceName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatMode(VideoMode mode) =>
        $"{mode.Width}×{mode.Height} {mode.FramesPerSecondNumerator / (double)mode.FramesPerSecondDenominator:0.##} FPS";

    private async Task<LocalControlResponse> ImportSnapshotFileAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var import = LocalControlProtocol.DeserializePayload<ImportSnapshotFileRequest>(request.Payload);
        var result = await transferService.ImportAsync(import.Path, import.TrustSigner, cancellationToken);
        SetOperationMessage($"已导入存档“{result.Name}”");
        return LocalControlProtocol.CreateSuccess(request.RequestId, result);
    }

    private async Task<LocalControlResponse> ExportSnapshotFileAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken)
    {
        var export = LocalControlProtocol.DeserializePayload<ExportSnapshotFileRequest>(request.Payload);
        var result = await transferService.ExportAsync(export.SnapshotId, export.Path, cancellationToken);
        SetOperationMessage($"已导出存档“{result.Name}”");
        return LocalControlProtocol.CreateSuccess(request.RequestId, result);
    }

    private string GetOperationMessage()
    {
        lock (stateLock)
        {
            return operationMessage;
        }
    }

    private void SetOperationMessage(string message)
    {
        lock (stateLock)
        {
            operationMessage = message;
        }
    }

    private static string CreateApplicationStatusMessage(ApplicationRuntimeStatus status)
    {
        if (!status.CanDetermineLiveState)
        {
            return status.IsRunning ? "无法确认直播状态" : "未连接";
        }

        if (status.IsStreaming)
        {
            return "正在推流";
        }

        if (status.IsRecording)
        {
            return "正在录制";
        }

        return status.IsRunning ? "已连接" : "未运行";
    }
}
