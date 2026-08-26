using LiveStudio.Cloud.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Infrastructure;

public static class OrganizationWriteLock
{
    public static Task AcquireAsync(
        ApplicationDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken) => dbContext.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT 1 FROM \"Organizations\" WHERE \"Id\" = {organizationId} FOR UPDATE",
        cancellationToken);
}
