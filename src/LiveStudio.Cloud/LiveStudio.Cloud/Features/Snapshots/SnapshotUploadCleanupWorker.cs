using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Snapshots;

public sealed class SnapshotUploadCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<SnapshotUploadCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> LogCleanupFailure = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2301, nameof(LogCleanupFailure)),
        "清理过期 Multipart Upload {UploadId} 失败");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            await CleanupAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var objectStorage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var expired = await dbContext.SnapshotUploads
            .Where(upload => upload.CompletedAt == null && upload.ExpiresAt < DateTimeOffset.UtcNow)
            .OrderBy(upload => upload.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var upload in expired)
        {
            try
            {
                await objectStorage.AbortMultipartUploadAsync(
                    upload.ObjectKey,
                    upload.MultipartUploadId,
                    cancellationToken);
                dbContext.SnapshotUploads.Remove(upload);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or DbUpdateException)
            {
                LogCleanupFailure(logger, upload.Id, exception);
            }
        }
    }
}
