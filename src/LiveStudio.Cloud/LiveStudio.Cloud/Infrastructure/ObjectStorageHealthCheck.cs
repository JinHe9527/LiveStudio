using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveStudio.Cloud.Infrastructure;

public sealed class ObjectStorageHealthCheck(IObjectStorage objectStorage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await objectStorage.GetMetadataAsync(".health/readiness", cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("对象存储不可访问", exception);
        }
    }
}
