using System.Security.Claims;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Realtime;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Organizations;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/organizations").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{organizationId:guid}/rooms", ListRoomsAsync);
        group.MapPost("/{organizationId:guid}/rooms", CreateRoomAsync);
        group.MapGet("/{organizationId:guid}/members", ListMembersAsync);
        group.MapGet("/{organizationId:guid}/audit-events", ListAuditEventsAsync);
        group.MapPost("/{organizationId:guid}/members", AddMemberAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.Unauthorized();
        }

        var organizations = await dbContext.OrganizationMembers
            .Where(member => member.UserId == userId)
            .Join(
                dbContext.Organizations,
                member => member.OrganizationId,
                organization => organization.Id,
                (_, organization) => organization)
            .OrderBy(organization => organization.Name)
            .Select(organization => new OrganizationSummary(organization.Id, organization.Name))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(organizations);
    }

    private static async Task<IResult> CreateAsync(
        CreateOrganizationRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.Unauthorized();
        }

        if (name.Length is 0 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Name)] = ["Organization 名称长度必须为 1 到 100 个字符"]
            });
        }

        var organization = new OrganizationEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMembers.Add(new OrganizationMemberEntity
        {
            OrganizationId = organization.Id,
            UserId = userId,
            Role = OrganizationRole.Owner
        });
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            ActorId = userId,
            Action = "organization.create",
            TargetType = "organization",
            TargetId = organization.Id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
            DetailJson = "{}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/organizations/{organization.Id}", new OrganizationSummary(organization.Id, organization.Name));
    }

    private static async Task<IResult> ListRoomsAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        DeviceConnectionRegistry connections,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var rooms = await dbContext.LiveRooms
            .Where(room => room.OrganizationId == organizationId)
            .OrderBy(room => room.Name)
            .ToListAsync(cancellationToken);
        var deviceIds = rooms.Where(room => room.DeviceId is not null).Select(room => room.DeviceId!.Value).ToArray();
        var devices = await dbContext.Devices.AsNoTracking()
            .Where(device => deviceIds.Contains(device.Id))
            .ToDictionaryAsync(device => device.Id, cancellationToken);
        var roomIds = rooms.Select(room => room.Id).ToArray();
        var currentHashes = await dbContext.CurrentParameterStates.AsNoTracking()
            .Where(state => state.OrganizationId == organizationId && roomIds.Contains(state.RoomId))
            .ToDictionaryAsync(state => state.RoomId, state => state.ParameterHash, cancellationToken);
        var latestSnapshotHashes = await dbContext.Snapshots.AsNoTracking()
            .Where(snapshot => snapshot.OrganizationId == organizationId && roomIds.Contains(snapshot.RoomId))
            .GroupBy(snapshot => snapshot.RoomId)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CreatedAt).First())
            .ToDictionaryAsync(snapshot => snapshot.RoomId, snapshot => snapshot.ParameterHash, cancellationToken);
        var response = rooms.Select(room => new LiveRoomSummary(
            room.Id,
            room.OrganizationId,
            room.Name,
            room.DeviceId,
            room.LastSnapshotAt,
            room.DeviceId is { } deviceId
                && devices.TryGetValue(deviceId, out var device)
                && connections.IsConnected(deviceId)
                && device.InteractiveUserSession
                && device.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-45),
            currentHashes.TryGetValue(room.Id, out var currentHash)
                && latestSnapshotHashes.TryGetValue(room.Id, out var snapshotHash)
                && !string.Equals(currentHash, snapshotHash, StringComparison.Ordinal)))
            .ToArray();
        return TypedResults.Ok(response);
    }

    private static async Task<IResult> CreateRoomAsync(
        Guid organizationId,
        CreateRoomRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Admin, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var name = request.Name.Trim();
        if (name.Length is 0 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Name)] = ["直播间名称长度必须为 1 到 100 个字符"]
            });
        }

        var room = new LiveRoomEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name
        };
        dbContext.LiveRooms.Add(room);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            Action = "room.created",
            TargetType = "LiveRoom",
            TargetId = room.Id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
            DetailJson = "{}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Created(
            $"/api/v1/organizations/{organizationId}/rooms/{room.Id}",
            new LiveRoomSummary(room.Id, organizationId, room.Name, null, null, false, false));
    }

    private static async Task<IResult> ListMembersAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Admin, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var members = await dbContext.OrganizationMembers.AsNoTracking()
            .Where(member => member.OrganizationId == organizationId)
            .Join(
                dbContext.Users,
                member => member.UserId,
                account => account.Id,
                (member, account) => new
                {
                    member.UserId,
                    account.Email,
                    account.UserName,
                    member.Role
                })
            .OrderBy(member => member.Email ?? member.UserName ?? member.UserId)
            .Select(member => new MembershipSummary(
                member.UserId,
                member.Email ?? member.UserName ?? member.UserId,
                member.Role))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(members);
    }

    private static async Task<IResult> ListAuditEventsAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Admin, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var events = await dbContext.AuditEvents.AsNoTracking()
            .Where(audit => audit.OrganizationId == organizationId)
            .OrderByDescending(audit => audit.OccurredAt)
            .Take(200)
            .Select(audit => new AuditEventSummary(
                audit.Id,
                audit.ActorId,
                audit.Action,
                audit.TargetType,
                audit.TargetId,
                audit.OccurredAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(events);
    }

    private static async Task<IResult> AddMemberAsync(
        Guid organizationId,
        AddMembershipRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Owner, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var account = await userManager.FindByEmailAsync(request.Email.Trim());
        if (account is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Email)] = ["该邮箱尚未注册 LiveStudio 账户"]
            });
        }

        var membership = await dbContext.OrganizationMembers.SingleOrDefaultAsync(
            member => member.OrganizationId == organizationId && member.UserId == account.Id,
            cancellationToken);
        if (membership is null)
        {
            dbContext.OrganizationMembers.Add(new OrganizationMemberEntity
            {
                OrganizationId = organizationId,
                UserId = account.Id,
                Role = request.Role
            });
        }
        else
        {
            membership.Role = request.Role;
        }

        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            Action = "membership.saved",
            TargetType = "Membership",
            TargetId = account.Id,
            OccurredAt = DateTimeOffset.UtcNow,
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { request.Role })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
