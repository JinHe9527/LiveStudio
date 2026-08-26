using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class SnapshotUploadWorker(
    LocalSnapshotIndex snapshotIndex,
    DeviceApiClient apiClient,
    ILogger<SnapshotUploadWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private static readonly Action<ILogger, Guid, Exception?> LogUploadFailure = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1101, nameof(LogUploadFailure)),
        "本地存档 {SnapshotId} 上传失败，联网后将重试");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            await SyncNowAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<LiveStudio.Contracts.SnapshotSyncResult> SyncNowAsync(
        CancellationToken cancellationToken)
    {
        await syncGate.WaitAsync(cancellationToken);
        try
        {
            await snapshotIndex.InitializeAsync(cancellationToken);
            var uploadedCount = 0;
            string? failureMessage = null;
            foreach (var snapshot in await snapshotIndex.GetPendingUploadsAsync(cancellationToken))
            {
                try
                {
                    await apiClient.UploadSnapshotAsync(snapshot, cancellationToken);
                    await snapshotIndex.MarkUploadedAsync(snapshot.Id, cancellationToken);
                    uploadedCount++;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    LogUploadFailure(logger, snapshot.Id, exception);
                    failureMessage = exception.Message;
                    break;
                }
            }

            var remainingCount = (await snapshotIndex.GetPendingUploadsAsync(cancellationToken)).Count;
            var message = failureMessage is not null
                ? $"已同步 {uploadedCount} 份，剩余 {remainingCount} 份：{failureMessage}"
                : remainingCount == 0
                    ? $"云存档同步完成，共上传 {uploadedCount} 份"
                    : $"已同步 {uploadedCount} 份，仍有 {remainingCount} 份等待下一轮";
            return new LiveStudio.Contracts.SnapshotSyncResult(uploadedCount, remainingCount, message);
        }
        finally
        {
            syncGate.Release();
        }
    }
}
