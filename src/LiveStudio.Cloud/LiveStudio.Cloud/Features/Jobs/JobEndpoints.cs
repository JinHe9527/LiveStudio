using System.Security.Claims;
using System.Text.Json;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Realtime;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Jobs;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var organizations = endpoints.MapGroup("/api/v1/organizations/{organizationId:guid}")
            .RequireAuthorization();
        organizations.MapPost("/capture-jobs", CreateCaptureAsync);
        organizations.MapPost("/restore-jobs", CreateRestoreAsync);
        organizations.MapPost("/refresh-jobs", CreateRefreshAsync);
        organizations.MapGet("/jobs", ListJobsAsync);
        organizations.MapGet("/jobs/{jobId:guid}", GetJobAsync);

        var devices = endpoints.MapGroup("/api/v1/devices/{deviceId:guid}/jobs")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = DeviceAuthenticationHandler.AuthenticationScheme
            });
        devices.MapPost("/{jobId:guid}/claim", ClaimAsync);
        devices.MapPost("/{jobId:guid}/events", ReportEventAsync);
        devices.MapGet("/available", ListAvailableAsync);
        return endpoints;
    }

    private static Task<IResult> CreateCaptureAsync(
        Guid organizationId,
        CreateCaptureJobRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        DeviceConnectionRegistry connections,
        IHubContext<AgentHub> hub,
        CancellationToken cancellationToken) => CreateAsync(
            organizationId,
            request.RoomId,
            request.DeviceId,
            null,
            JobKind.Capture,
            CompatibilityLevel.Verified,
            request.Name,
            user,
            access,
            dbContext,
            connections,
            hub,
            cancellationToken);

    private static Task<IResult> CreateRestoreAsync(
        Guid organizationId,
        CreateRestoreJobRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        DeviceConnectionRegistry connections,
        IHubContext<AgentHub> hub,
        CancellationToken cancellationToken) => CreateAsync(
            organizationId,
            request.RoomId,
            request.DeviceId,
            request.SnapshotId,
            JobKind.Restore,
            null,
            "恢复存档",
            user,
            access,
            dbContext,
            connections,
            hub,
            cancellationToken);

    private static Task<IResult> CreateRefreshAsync(
        Guid organizationId,
        CreateRefreshPreviewJobRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        DeviceConnectionRegistry connections,
        IHubContext<AgentHub> hub,
        CancellationToken cancellationToken) => CreateAsync(
            organizationId,
            request.RoomId,
            request.DeviceId,
            null,
            JobKind.RefreshPreview,
            CompatibilityLevel.Verified,
            "刷新当前参数与画面",
            user,
            access,
            dbContext,
            connections,
            hub,
            cancellationToken);

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        Guid roomId,
        Guid deviceId,
        Guid? snapshotId,
        JobKind kind,
        CompatibilityLevel? compatibility,
        string message,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        DeviceConnectionRegistry connections,
        IHubContext<AgentHub> hub,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Operator, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var device = await dbContext.Devices.SingleOrDefaultAsync(
            value => value.Id == deviceId
                && value.OrganizationId == organizationId
                && value.RoomId == roomId,
            cancellationToken);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        if (snapshotId is not null && !await dbContext.Snapshots.AnyAsync(
                snapshot => snapshot.Id == snapshotId
                    && snapshot.OrganizationId == organizationId,
                cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var assessedCompatibility = kind == JobKind.Restore && snapshotId is { } restoreSnapshotId
            ? await AssessRestoreCompatibilityAsync(
                organizationId,
                deviceId,
                restoreSnapshotId,
                dbContext,
                cancellationToken)
            : compatibility ?? CompatibilityLevel.Verified;
        if (assessedCompatibility == CompatibilityLevel.Unsupported)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "IncompatibleVersion",
                detail: "目标设备没有可验证或可实验匹配的应用结构");
        }

        var online = connections.IsConnected(deviceId)
            && device.InteractiveUserSession
            && device.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-45);
        if (!online)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "DeviceOffline",
                detail: "目标电脑离线，任务不会排队");
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var job = new RemoteJobEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RoomId = roomId,
            DeviceId = deviceId,
            SnapshotId = snapshotId,
            Kind = kind,
            Status = JobStatus.Queued,
            Compatibility = assessedCompatibility,
            RequestedBy = userId,
            CreatedAt = now,
            Message = message
        };
        dbContext.RemoteJobs.Add(job);
        dbContext.JobEvents.Add(new JobEventEntity
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            ExecutionId = Guid.Empty,
            Sequence = 0,
            Status = JobStatus.Queued,
            OccurredAt = now,
            Message = message
        });
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = userId,
            Action = kind switch
            {
                JobKind.Capture => "capture.requested",
                JobKind.Restore => "restore.requested",
                JobKind.RefreshPreview => "preview-refresh.requested",
                _ => throw new InvalidOperationException($"不支持的任务类型 {kind}")
            },
            TargetType = "RemoteJob",
            TargetId = job.Id.ToString(),
            OccurredAt = now,
            DetailJson = JsonSerializer.Serialize(new { job.RoomId, job.DeviceId, job.SnapshotId, job.Compatibility })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group(AgentHub.DeviceGroup(organizationId, deviceId))
            .SendAsync("JobAvailable", new AgentJobNotification(job.Id, job.Kind), cancellationToken);
        return TypedResults.Created(
            $"/api/v1/organizations/{organizationId}/jobs/{job.Id}",
            ToContract(job));
    }

    private static async Task<CompatibilityLevel> AssessRestoreCompatibilityAsync(
        Guid organizationId,
        Guid deviceId,
        Guid snapshotId,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var currentParametersJson = await dbContext.CurrentParameterStates.AsNoTracking()
            .Where(state => state.OrganizationId == organizationId && state.DeviceId == deviceId)
            .Select(state => state.ParametersJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (currentParametersJson is null)
        {
            return CompatibilityLevel.Unsupported;
        }

        var currentState = JsonSerializer.Deserialize<CurrentParameterState>(currentParametersJson);
        if (currentState is null)
        {
            return CompatibilityLevel.Unsupported;
        }

        var snapshotParameters = await dbContext.SnapshotComponents.AsNoTracking()
            .Where(component => component.OrganizationId == organizationId && component.SnapshotId == snapshotId)
            .Select(component => component.ParametersJson)
            .ToListAsync(cancellationToken);
        if (snapshotParameters.Count == 0)
        {
            return CompatibilityLevel.Unsupported;
        }

        var snapshotApplications = snapshotParameters
            .Select(value => JsonSerializer.Deserialize<ApplicationSnapshot>(value))
            .ToArray();
        if (snapshotApplications.Any(application => application is null))
        {
            return CompatibilityLevel.Unsupported;
        }

        var catalog = await dbContext.AdapterCatalog.AsNoTracking()
            .Where(adapter => adapter.Application == ApplicationKind.LiveCompanion)
            .ToListAsync(cancellationToken);
        var result = CompatibilityLevel.Verified;
        foreach (var snapshotApplication in snapshotApplications.Cast<ApplicationSnapshot>())
        {
            var targetApplication = currentState.Applications.FirstOrDefault(
                application => application.Kind == snapshotApplication.Kind);
            if (targetApplication is null)
            {
                return CompatibilityLevel.Unsupported;
            }

            if (snapshotApplication.Kind == ApplicationKind.Obs)
            {
                if (!string.Equals(snapshotApplication.Version, targetApplication.Version, StringComparison.Ordinal))
                {
                    result = CompatibilityLevel.Experimental;
                }

                continue;
            }

            var structureMatches = catalog.Where(adapter => string.Equals(
                    adapter.StructureFingerprint,
                    targetApplication.StructureFingerprint,
                    StringComparison.Ordinal))
                .ToArray();
            if (structureMatches.Length == 0
                || !Version.TryParse(targetApplication.Version, out var targetVersion))
            {
                return CompatibilityLevel.Unsupported;
            }

            var verified = structureMatches.Any(adapter =>
                adapter.Verified
                && Version.TryParse(adapter.MinimumVersion, out var minimumVersion)
                && Version.TryParse(adapter.MaximumVersion, out var maximumVersion)
                && minimumVersion <= targetVersion
                && maximumVersion >= targetVersion);
            if (!verified)
            {
                result = CompatibilityLevel.Experimental;
            }
        }

        return result;
    }

    private static async Task<IResult> GetJobAsync(
        Guid organizationId,
        Guid jobId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var job = await dbContext.RemoteJobs.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == jobId && value.OrganizationId == organizationId,
            cancellationToken);
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        var events = await dbContext.JobEvents.AsNoTracking()
            .Where(value => value.JobId == jobId)
            .OrderBy(value => value.OccurredAt)
            .Select(value => new JobEvent(
                value.Id,
                value.JobId,
                value.ExecutionId,
                value.Sequence,
                value.Status,
                value.OccurredAt,
                value.Message,
                value.DetailCode,
                value.VerificationDetail))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new { Job = ToContract(job), Events = events });
    }

    private static async Task<IResult> ListJobsAsync(
        Guid organizationId,
        Guid? roomId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var query = dbContext.RemoteJobs.AsNoTracking().Where(job => job.OrganizationId == organizationId);
        if (roomId is not null)
        {
            query = query.Where(job => job.RoomId == roomId);
        }

        var jobs = await query
            .OrderByDescending(job => job.CreatedAt)
            .Take(200)
            .Select(job => new JobSummary(
                job.Id,
                job.RoomId,
                job.DeviceId,
                job.SnapshotId,
                job.Kind,
                job.Status,
                job.Compatibility,
                job.CreatedAt,
                job.CompletedAt,
                job.Message,
                job.DetailCode))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(jobs);
    }

    private static async Task<IResult> ClaimAsync(
        Guid deviceId,
        Guid jobId,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        var organizationId = GetOrganizationId(user);
        var leaseUntil = now.AddMinutes(2);
        var executionId = Guid.NewGuid();
        var changed = await dbContext.RemoteJobs
            .Where(job => job.Id == jobId
                && job.DeviceId == deviceId
                && job.OrganizationId == organizationId
                && job.Status == JobStatus.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, JobStatus.Claimed)
                    .SetProperty(job => job.ClaimedAt, now)
                    .SetProperty(job => job.ExecutionId, executionId)
                    .SetProperty(job => job.LastEventSequence, 1)
                    .SetProperty(job => job.LeaseUntil, leaseUntil),
                cancellationToken);
        if (changed != 1)
        {
            return TypedResults.Conflict();
        }

        var job = await dbContext.RemoteJobs.AsNoTracking().SingleAsync(
            value => value.Id == jobId && value.OrganizationId == organizationId,
            cancellationToken);
        dbContext.JobEvents.Add(new JobEventEntity
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            ExecutionId = executionId,
            Sequence = 1,
            Status = JobStatus.Claimed,
            OccurredAt = now,
            Message = "Agent 已领取任务"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new ClaimJobResponse(
            job.Id,
            executionId,
            job.Kind,
            job.Message ?? (job.Kind == JobKind.Capture ? "画面存档" : "恢复存档"),
            job.RoomId,
            job.DeviceId,
            job.SnapshotId,
            job.Compatibility,
            leaseUntil));
    }

    private static async Task<IResult> ListAvailableAsync(
        Guid deviceId,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var organizationId = GetOrganizationId(user);
        var jobs = await dbContext.RemoteJobs.AsNoTracking()
            .Where(job => job.DeviceId == deviceId
                && job.OrganizationId == organizationId
                && job.Status == JobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .Select(job => new AgentJobNotification(job.Id, job.Kind))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(jobs);
    }

    private static async Task<IResult> ReportEventAsync(
        Guid deviceId,
        Guid jobId,
        ReportJobEventRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var organizationId = GetOrganizationId(user);
        var job = await dbContext.RemoteJobs.SingleOrDefaultAsync(
            value => value.Id == jobId
                && value.DeviceId == deviceId
                && value.OrganizationId == organizationId,
            cancellationToken);
        if (job is null)
        {
            return TypedResults.NotFound();
        }


        if (job.ExecutionId != request.ExecutionId)
        {
            return TypedResults.Conflict("任务执行标识不一致");
        }

        if (request.Sequence <= job.LastEventSequence)
        {
            var existing = await dbContext.JobEvents.AsNoTracking().SingleOrDefaultAsync(value =>
                value.JobId == jobId
                && value.ExecutionId == request.ExecutionId
                && value.Sequence == request.Sequence,
                cancellationToken);
            var isSameEvent = existing is not null
                && existing.Status == request.Status
                && string.Equals(existing.Message, request.Message, StringComparison.Ordinal)
                && string.Equals(existing.DetailCode, request.DetailCode, StringComparison.Ordinal)
                && string.Equals(existing.VerificationDetail, request.VerificationDetail, StringComparison.Ordinal);
            return isSameEvent
                ? TypedResults.NoContent()
                : TypedResults.Conflict("任务事件序号无效或事件内容不一致");
        }

        if (request.Sequence != job.LastEventSequence + 1)
        {
            return TypedResults.Conflict("任务事件必须按顺序上报");
        }

        if (!JobTransitionRules.CanTransition(job.Status, request.Status))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "InvalidJobTransition",
                detail: $"不允许从 {job.Status} 转换到 {request.Status}");
        }

        var now = DateTimeOffset.UtcNow;
        job.Status = request.Status;
        job.Message = request.Message;
        job.DetailCode = request.DetailCode;
        job.LastEventSequence = request.Sequence;
        job.LeaseUntil = JobTransitionRules.IsTerminal(request.Status) ? null : now.AddMinutes(2);
        if (JobTransitionRules.IsTerminal(request.Status))
        {
            job.CompletedAt = now;
            dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ActorId = $"device:{deviceId:N}",
                Action = job.Kind switch
                {
                    JobKind.Capture => "capture.completed",
                    JobKind.Restore => "restore.completed",
                    JobKind.RefreshPreview => "preview-refresh.completed",
                    _ => throw new InvalidOperationException($"不支持的任务类型 {job.Kind}")
                },
                TargetType = "RemoteJob",
                TargetId = job.Id.ToString(),
                OccurredAt = now,
                DetailJson = JsonSerializer.Serialize(new { request.Status, request.DetailCode })
            });
        }

        dbContext.JobEvents.Add(new JobEventEntity
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            ExecutionId = request.ExecutionId,
            Sequence = request.Sequence,
            Status = request.Status,
            OccurredAt = now,
            Message = request.Message,
            DetailCode = request.DetailCode,
            VerificationDetail = request.VerificationDetail
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static object ToContract(RemoteJobEntity job) => new
    {
        job.Id,
        job.OrganizationId,
        job.RoomId,
        job.DeviceId,
        job.SnapshotId,
        job.Kind,
        job.Status,
        job.Compatibility,
        job.CreatedAt,
        job.CompletedAt,
        job.Message,
        job.DetailCode
    };

    private static bool IsSameDevice(ClaimsPrincipal user, Guid deviceId) => string.Equals(
        user.FindFirstValue(DeviceAuthenticationHandler.DeviceIdClaim),
        deviceId.ToString(),
        StringComparison.OrdinalIgnoreCase);

    private static Guid GetOrganizationId(ClaimsPrincipal user) => Guid.TryParse(
        user.FindFirstValue(DeviceAuthenticationHandler.OrganizationIdClaim),
        out var organizationId)
        ? organizationId
        : Guid.Empty;
}
