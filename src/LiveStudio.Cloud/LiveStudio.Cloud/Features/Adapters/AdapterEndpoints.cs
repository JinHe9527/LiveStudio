using System.Security.Claims;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Adapters;

public static class AdapterEndpoints
{
    public static IEndpointRouteBuilder MapAdapterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/organizations/{organizationId:guid}/adapters", ListAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var adapters = await dbContext.AdapterCatalog.AsNoTracking()
            .OrderBy(adapter => adapter.Application)
            .ThenByDescending(adapter => adapter.PublishedAt)
            .Select(adapter => new AdapterCatalogSummary(
                adapter.Id,
                adapter.Application,
                adapter.MinimumVersion,
                adapter.MaximumVersion,
                adapter.StructureFingerprint,
                adapter.Verified,
                adapter.PublishedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(adapters);
    }
}
