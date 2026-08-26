using LiveStudio.Cloud.Data;
using LiveStudio.Contracts;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Organizations;

public sealed partial class FirstWorkspaceBootstrapper(
    ApplicationDbContext dbContext,
    ILogger<FirstWorkspaceBootstrapper> logger)
{
    public const string DefaultWorkspaceName = "默认直播管理空间";
    public const string DefaultRoomName = "直播间 1";

    public async Task<bool> EnsureForFirstAccountAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"Organizations\" IN SHARE ROW EXCLUSIVE MODE",
            cancellationToken);

        if (await dbContext.Organizations.AnyAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var organization = new OrganizationEntity
        {
            Id = Guid.NewGuid(),
            Name = DefaultWorkspaceName,
            CreatedAt = now
        };
        var room = new LiveRoomEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Name = DefaultRoomName
        };

        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMembers.Add(new OrganizationMemberEntity
        {
            OrganizationId = organization.Id,
            UserId = userId,
            Role = OrganizationRole.Owner
        });
        dbContext.LiveRooms.Add(room);
        dbContext.AuditEvents.AddRange(
            new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                ActorId = userId,
                Action = "organization.create",
                TargetType = "organization",
                TargetId = organization.Id.ToString(),
                OccurredAt = now,
                DetailJson = "{\"source\":\"first-account-bootstrap\"}"
            },
            new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                ActorId = userId,
                Action = "room.created",
                TargetType = "LiveRoom",
                TargetId = room.Id.ToString(),
                OccurredAt = now,
                DetailJson = "{\"source\":\"first-account-bootstrap\"}"
            });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogWorkspaceCreated(organization.Id, room.Id, userId);
        return true;
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Created the initial LiveStudio workspace {OrganizationId} and room {RoomId} for account {UserId}.")]
    private partial void LogWorkspaceCreated(Guid organizationId, Guid roomId, string userId);
}
