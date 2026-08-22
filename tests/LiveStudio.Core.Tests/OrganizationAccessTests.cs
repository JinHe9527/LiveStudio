using System.Security.Claims;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Core.Tests;

public sealed class OrganizationAccessTests
{
    [Fact]
    public async Task MembershipNeverGrantsAccessToAnotherOrganization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var allowedOrganizationId = Guid.NewGuid();
        var deniedOrganizationId = Guid.NewGuid();
        dbContext.Organizations.AddRange(
            new OrganizationEntity
            {
                Id = allowedOrganizationId,
                Name = "A",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new OrganizationEntity
            {
                Id = deniedOrganizationId,
                Name = "B",
                CreatedAt = DateTimeOffset.UtcNow
            });
        dbContext.OrganizationMembers.Add(new OrganizationMemberEntity
        {
            OrganizationId = allowedOrganizationId,
            UserId = "operator-a",
            Role = OrganizationRole.Operator
        });
        await dbContext.SaveChangesAsync();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "operator-a")],
            "test"));
        var access = new OrganizationAccessService(dbContext);

        Assert.True(await access.HasRoleAsync(
            user,
            allowedOrganizationId,
            OrganizationRole.Operator,
            CancellationToken.None));
        Assert.False(await access.HasRoleAsync(
            user,
            allowedOrganizationId,
            OrganizationRole.Admin,
            CancellationToken.None));
        Assert.False(await access.HasRoleAsync(
            user,
            deniedOrganizationId,
            OrganizationRole.Viewer,
            CancellationToken.None));
    }
}
