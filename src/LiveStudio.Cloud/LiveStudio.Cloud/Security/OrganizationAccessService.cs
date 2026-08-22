using System.Security.Claims;
using LiveStudio.Cloud.Data;
using LiveStudio.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Security;

public sealed class OrganizationAccessService(ApplicationDbContext dbContext)
{
    public async Task<bool> HasRoleAsync(
        ClaimsPrincipal user,
        Guid organizationId,
        OrganizationRole minimumRole,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var role = await dbContext.OrganizationMembers
            .Where(member => member.OrganizationId == organizationId && member.UserId == userId)
            .Select(member => (OrganizationRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);
        return role is not null && role.Value <= minimumRole;
    }
}
