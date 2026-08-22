using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class LanSnapshotWorker(
    LanSnapshotConfigurationStore configurationStore,
    LocalSnapshotIndex snapshotIndex,
    SnapshotTransferService transferService,
    ILogger<LanSnapshotWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogPackageSkipped = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1301, nameof(LogPackageSkipped)),
        "局域网存档 {PackagePath} 未导入");
    private static readonly Action<ILogger, Exception?> LogSyncFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1302, nameof(LogSyncFailure)),
        "局域网存档同步失败");
    private readonly Lock statusLock = new();
    private string status = "未配置";

    public string Status
    {
        get
        {
            lock (statusLock)
            {
                return status;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await snapshotIndex.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            await SynchronizeSafelyAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SynchronizeSafelyAsync(CancellationToken cancellationToken)
    {
        var sharedDirectory = configurationStore.SharedDirectory;
        if (string.IsNullOrWhiteSpace(sharedDirectory))
        {
            SetStatus("未配置");
            return;
        }

        if (!Directory.Exists(sharedDirectory))
        {
            SetStatus("共享目录当前不可用");
            return;
        }

        try
        {
            foreach (var snapshot in await snapshotIndex.GetAllAsync(cancellationToken))
            {
                await SnapshotTransferService.PublishAsync(snapshot, sharedDirectory, cancellationToken);
            }

            var skipped = 0;
            foreach (var packagePath in Directory.EnumerateFiles(
                         sharedDirectory,
                         "*.lscfg",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    await transferService.ImportAsync(packagePath, trustSigner: false, cancellationToken);
                }
                catch (Exception exception) when (exception is SnapshotSignerTrustRequiredException
                    or LiveStudio.Packaging.SnapshotPackageException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    skipped++;
                    LogPackageSkipped(logger, packagePath, exception);
                }
            }

            SetStatus(skipped == 0 ? "已同步" : $"已同步，{skipped} 份存档需要处理");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or LiveStudio.Packaging.SnapshotPackageException)
        {
            SetStatus(exception.Message);
            LogSyncFailure(logger, exception);
        }
    }

    private void SetStatus(string value)
    {
        lock (statusLock)
        {
            status = value;
        }
    }
}
