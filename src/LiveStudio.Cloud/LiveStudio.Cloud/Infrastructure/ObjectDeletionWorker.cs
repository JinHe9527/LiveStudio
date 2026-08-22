using LiveStudio.Cloud.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Infrastructure;

public sealed class ObjectDeletionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ObjectDeletionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, int, Exception?> LogDeletionFailure = LoggerMessage.Define<string, int>(
        LogLevel.Warning,
        new EventId(2301, nameof(LogDeletionFailure)),
        "对象 {ObjectKey} 第 {AttemptCount} 次删除失败");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            await ProcessPendingAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var objectStorage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var now = DateTimeOffset.UtcNow;
        var pending = await dbContext.ObjectDeletions.AsNoTracking()
            .Where(value => value.NextAttemptAt <= now)
            .OrderBy(value => value.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var item in pending)
        {
            try
            {
                await objectStorage.DeleteAsync(item.ObjectKey, cancellationToken);
                await dbContext.ObjectDeletions
                    .Where(value => value.Id == item.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                deleted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var attemptCount = item.AttemptCount + 1;
                var retryDelay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attemptCount, 6))));
                await dbContext.ObjectDeletions
                    .Where(value => value.Id == item.Id)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(value => value.AttemptCount, attemptCount)
                            .SetProperty(value => value.NextAttemptAt, now.Add(retryDelay))
                            .SetProperty(value => value.LastError, exception.Message[..Math.Min(exception.Message.Length, 1000)]),
                        cancellationToken);
                LogDeletionFailure(logger, item.ObjectKey, attemptCount, exception);
            }
        }

        return deleted;
    }
}
