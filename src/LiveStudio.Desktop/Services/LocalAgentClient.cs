using System.IO.Pipes;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.Services;

public sealed class LocalAgentClient
{
    private readonly TimeSpan connectionTimeout = TimeSpan.FromSeconds(2);

    public Task<LocalAgentState> GetStateAsync(CancellationToken cancellationToken) => SendAsync<object, LocalAgentState>(
        LocalControlMethod.GetState,
        new { },
        cancellationToken);

    public Task<LocalOperationProgress> GetOperationProgressAsync(CancellationToken cancellationToken) =>
        SendAsync<object, LocalOperationProgress>(
            LocalControlMethod.GetOperationProgress,
            new { },
            cancellationToken);

    public Task<LocalAgentState> RefreshCurrentStateAsync(CancellationToken cancellationToken) =>
        SendAsync<object, LocalAgentState>(
            LocalControlMethod.RefreshCurrentState,
            new { },
            cancellationToken);

    public Task<LocalSnapshotOperationResult> CaptureAsync(
        string name,
        IReadOnlyList<CameraStationSnapshot>? cameraStations,
        IReadOnlyList<CameraReferenceImageChange>? imageChanges,
        CancellationToken cancellationToken) => SendAsync<CaptureLocalSnapshotRequest, LocalSnapshotOperationResult>(
            LocalControlMethod.CaptureSnapshot,
            new CaptureLocalSnapshotRequest(name, cameraStations, imageChanges),
            cancellationToken);

    public Task<LocalSnapshotOperationResult> CaptureAsync(
        string name,
        CancellationToken cancellationToken) => CaptureAsync(name, null, null, cancellationToken);

    public Task<LocalSnapshotOperationResult> UpdateSnapshotCameraStationsAsync(
        Guid snapshotId,
        IReadOnlyList<CameraStationSnapshot> cameraStations,
        CancellationToken cancellationToken) => UpdateSnapshotCameraStationsAsync(
            snapshotId,
            cameraStations,
            [],
            cancellationToken);

    public Task<LocalSnapshotOperationResult> UpdateSnapshotCameraStationsAsync(
        Guid snapshotId,
        IReadOnlyList<CameraStationSnapshot> cameraStations,
        IReadOnlyList<CameraReferenceImageChange> imageChanges,
        CancellationToken cancellationToken) => SendAsync<UpdateSnapshotCameraStationsRequest, LocalSnapshotOperationResult>(
            LocalControlMethod.UpdateSnapshotCameraStations,
            new UpdateSnapshotCameraStationsRequest(snapshotId, cameraStations, imageChanges),
            cancellationToken);

    public Task<LocalSnapshotPreview> GetCameraReferenceImageAsync(
        Guid snapshotId,
        int slot,
        CancellationToken cancellationToken) => SendAsync<GetCameraReferenceImageRequest, LocalSnapshotPreview>(
            LocalControlMethod.GetCameraReferenceImage,
            new GetCameraReferenceImageRequest(snapshotId, slot),
            cancellationToken);

    public Task<LocalSnapshotOperationResult> RestoreAsync(
        Guid snapshotId,
        CancellationToken cancellationToken) => SendAsync<RestoreLocalSnapshotRequest, LocalSnapshotOperationResult>(
            LocalControlMethod.RestoreSnapshot,
            new RestoreLocalSnapshotRequest(snapshotId),
            cancellationToken);

    public Task<LocalAgentState> ConfigureObsAsync(
        Uri endpoint,
        string password,
        CancellationToken cancellationToken) => SendAsync<ConfigureObsRequest, LocalAgentState>(
            LocalControlMethod.ConfigureObs,
            new ConfigureObsRequest(endpoint, password),
            cancellationToken);

    public Task<LocalAgentState> AutoConfigureObsAsync(CancellationToken cancellationToken) =>
        SendAsync<object, LocalAgentState>(
            LocalControlMethod.AutoConfigureObs,
            new { },
            cancellationToken);

    public Task<LocalAgentState> ConfigureLanDirectoryAsync(
        string? path,
        CancellationToken cancellationToken) => SendAsync<ConfigureLanDirectoryRequest, LocalAgentState>(
            LocalControlMethod.ConfigureLanDirectory,
            new ConfigureLanDirectoryRequest(path),
            cancellationToken);

    public Task<LocalAgentState> ConfigureAutoStartAsync(
        bool enabled,
        CancellationToken cancellationToken) => SendAsync<ConfigureAutoStartRequest, LocalAgentState>(
            LocalControlMethod.ConfigureAutoStart,
            new ConfigureAutoStartRequest(enabled),
            cancellationToken);

    public Task<LocalAgentState> EnrollDeviceAsync(
        Uri serviceUri,
        string enrollmentToken,
        string deviceName,
        CancellationToken cancellationToken) => SendAsync<EnrollLocalDeviceRequest, LocalAgentState>(
            LocalControlMethod.EnrollDevice,
            new EnrollLocalDeviceRequest(serviceUri, enrollmentToken, deviceName),
            cancellationToken);

    public Task<LocalMappingContext> GetMappingContextAsync(
        Guid snapshotId,
        CancellationToken cancellationToken) => SendAsync<GetLocalMappingContextRequest, LocalMappingContext>(
            LocalControlMethod.GetMappingContext,
            new GetLocalMappingContextRequest(snapshotId),
            cancellationToken);

    public Task<CombinedSnapshot> GetSnapshotDetailAsync(
        Guid snapshotId,
        CancellationToken cancellationToken) => SendAsync<GetLocalSnapshotDetailRequest, CombinedSnapshot>(
            LocalControlMethod.GetSnapshotDetail,
            new GetLocalSnapshotDetailRequest(snapshotId),
            cancellationToken);

    public Task<LocalSnapshotPreview> GetSnapshotPreviewAsync(
        Guid snapshotId,
        ApplicationKind application,
        CancellationToken cancellationToken) => SendAsync<GetLocalSnapshotPreviewRequest, LocalSnapshotPreview>(
            LocalControlMethod.GetSnapshotPreview,
            new GetLocalSnapshotPreviewRequest(snapshotId, application),
            cancellationToken);

    public Task<LocalMappingContext> SaveDeviceMappingAsync(
        SaveLocalDeviceMappingRequest request,
        CancellationToken cancellationToken) => SendAsync<SaveLocalDeviceMappingRequest, LocalMappingContext>(
            LocalControlMethod.SaveDeviceMapping,
            request,
            cancellationToken);

    public Task<SnapshotImportPreview> InspectSnapshotFileAsync(
        string path,
        CancellationToken cancellationToken) => SendAsync<SnapshotFileRequest, SnapshotImportPreview>(
            LocalControlMethod.InspectSnapshotFile,
            new SnapshotFileRequest(path),
            cancellationToken);

    public Task<SnapshotTransferResult> ImportSnapshotFileAsync(
        string path,
        bool trustSigner,
        CancellationToken cancellationToken) => SendAsync<ImportSnapshotFileRequest, SnapshotTransferResult>(
            LocalControlMethod.ImportSnapshotFile,
            new ImportSnapshotFileRequest(path, trustSigner),
            cancellationToken);

    public Task<SnapshotTransferResult> ExportSnapshotFileAsync(
        Guid snapshotId,
        string path,
        CancellationToken cancellationToken) => SendAsync<ExportSnapshotFileRequest, SnapshotTransferResult>(
            LocalControlMethod.ExportSnapshotFile,
            new ExportSnapshotFileRequest(snapshotId, path),
            cancellationToken);

    public Task<DeleteSnapshotsResult> DeleteSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken) => SendAsync<DeleteLocalSnapshotRequest, DeleteSnapshotsResult>(
            LocalControlMethod.DeleteSnapshot,
            new DeleteLocalSnapshotRequest(snapshotId),
            cancellationToken);

    public Task<LocalSnapshotOperationResult> RenameSnapshotAsync(
        Guid snapshotId,
        string name,
        CancellationToken cancellationToken) => SendAsync<RenameLocalSnapshotRequest, LocalSnapshotOperationResult>(
            LocalControlMethod.RenameSnapshot,
            new RenameLocalSnapshotRequest(snapshotId, name),
            cancellationToken);

    public Task<DeleteSnapshotsResult> DeleteAllSnapshotsAsync(CancellationToken cancellationToken) =>
        SendAsync<object, DeleteSnapshotsResult>(
            LocalControlMethod.DeleteAllSnapshots,
            new { },
            cancellationToken);

    public Task<SnapshotSyncResult> SyncPendingSnapshotsAsync(CancellationToken cancellationToken) =>
        SendAsync<object, SnapshotSyncResult>(
            LocalControlMethod.SyncPendingSnapshots,
            new { },
            cancellationToken);

    private async Task<TResult> SendAsync<TRequest, TResult>(
        LocalControlMethod method,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("本机 Agent 控制只在 Windows 客户端可用");
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            LocalControlProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectionTimeout);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalControlException("AgentUnavailable", "没有找到当前用户会话中的 LiveStudio Agent");
        }

        var request = LocalControlProtocol.CreateRequest(method, payload);
        await LocalControlProtocol.WriteAsync(pipe, request, cancellationToken);
        var response = await LocalControlProtocol.ReadAsync<LocalControlResponse>(pipe, cancellationToken);
        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("本机执行端响应与请求不匹配");
        }

        return LocalControlProtocol.DeserializeResult<TResult>(response);
    }
}
