using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

public sealed class CloudAgentRuntime(
    IServiceProvider services,
    IDeviceCredentialStore credentialStore,
    ILogger<CloudAgentRuntime> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogActivationFailure = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1401, nameof(LogActivationFailure)),
        "Agent 云端运行时启动失败，等待凭据修复后重试");
    private static readonly Action<ILogger, Exception?> LogShutdownFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1402, nameof(LogShutdownFailure)),
        "Agent 云端运行时未能正常停止后台组件");
    private readonly Lock stateLock = new();
    private CurrentStatePublisher? currentStatePublisher;
    private SnapshotUploadWorker? snapshotUploadWorker;

    public async Task<bool> PublishCurrentStateAsync(
        LiveStudio.Contracts.CurrentStateReason reason,
        CancellationToken cancellationToken)
    {
        CurrentStatePublisher? publisher;
        lock (stateLock)
        {
            publisher = currentStatePublisher;
        }

        if (publisher is null)
        {
            return false;
        }

        await publisher.PublishAsync(reason, cancellationToken);
        return true;
    }

    public async Task<LiveStudio.Contracts.SnapshotSyncResult?> SyncSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        SnapshotUploadWorker? worker;
        lock (stateLock)
        {
            worker = snapshotUploadWorker;
        }

        return worker is null ? null : await worker.SyncNowAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            AgentWorker? worker = null;
            SnapshotUploadWorker? uploadWorker = null;
            try
            {
                if (!credentialStore.TryLoad(out var credentials) || !credentials.IsCloudEnrolled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                worker = services.GetRequiredService<AgentWorker>();
                uploadWorker = services.GetRequiredService<SnapshotUploadWorker>();
                var publisher = services.GetRequiredService<CurrentStatePublisher>();
                lock (stateLock)
                {
                    currentStatePublisher = publisher;
                    snapshotUploadWorker = uploadWorker;
                }

                await worker.StartAsync(stoppingToken);
                await uploadWorker.StartAsync(stoppingToken);
                var workerTask = worker.ExecuteTask
                    ?? throw new InvalidOperationException("AgentWorker 未启动执行任务");
                var uploadTask = uploadWorker.ExecuteTask
                    ?? throw new InvalidOperationException("SnapshotUploadWorker 未启动执行任务");
                var completedTask = await Task.WhenAny(workerTask, uploadTask);
                await completedTask;
                throw new InvalidOperationException("Agent 云端后台组件意外停止");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogActivationFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            finally
            {
                lock (stateLock)
                {
                    currentStatePublisher = null;
                    snapshotUploadWorker = null;
                }

                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (uploadWorker is not null)
                    {
                        await uploadWorker.StopAsync(stopTimeout.Token);
                    }

                    if (worker is not null)
                    {
                        await worker.StopAsync(stopTimeout.Token);
                    }
                }
                catch (Exception exception)
                {
                    LogShutdownFailure(logger, exception);
                }
            }
        }
    }
}
