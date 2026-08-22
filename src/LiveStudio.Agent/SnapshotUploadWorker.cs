using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class SnapshotUploadWorker(
    LocalSnapshotIndex snapshotIndex,
    DeviceApiClient apiClient,
    ILogger<SnapshotUploadWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> LogUploadFailure = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1101, nameof(LogUploadFailure)),
        "本地存档 {SnapshotId} 上传失败，联网后将重试");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await snapshotIndex.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            foreach (var snapshot in await snapshotIndex.GetPendingUploadsAsync(stoppingToken))
            {
                try
                {
                    await apiClient.UploadSnapshotAsync(snapshot, stoppingToken);
                    await snapshotIndex.MarkUploadedAsync(snapshot.Id, stoppingToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    LogUploadFailure(logger, snapshot.Id, exception);
                    break;
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
