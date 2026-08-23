using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Infrastructure;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Devices;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var organizations = endpoints.MapGroup("/api/v1/organizations/{organizationId:guid}")
            .RequireAuthorization();
        organizations.MapGet("/devices", ListDevicesAsync);
        organizations.MapGet("/devices/{deviceId:guid}/management-state", GetManagementStateAsync);
        organizations.MapGet("/device-mappings", ListMappingsAsync);
        organizations.MapPost("/device-enrollments", CreateEnrollmentAsync);
        organizations.MapPut("/devices/{deviceId:guid}/mappings", SaveMappingAsync);

        endpoints.MapPost("/api/v1/devices/enroll", EnrollAsync);
        var devices = endpoints.MapGroup("/api/v1/devices/{deviceId:guid}")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = DeviceAuthenticationHandler.AuthenticationScheme
            });
        devices.MapPost("/heartbeat", HeartbeatAsync);
        devices.MapPut("/current-state", UpdateCurrentStateAsync);
        devices.MapGet("/mappings", ListDeviceMappingsAsync);
        return endpoints;
    }

    private static async Task<IResult> ListDevicesAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        LiveStudio.Cloud.Realtime.DeviceConnectionRegistry connections,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var devices = await dbContext.Devices
            .Where(device => device.OrganizationId == organizationId)
            .OrderBy(device => device.Name)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return TypedResults.Ok(devices.Select(device => new DeviceSummary(
            device.Id,
            device.RoomId,
            device.Name,
            device.MachineName,
            device.AgentVersion,
            device.OperatingSystem,
            device.LastSeenAt,
            device.InteractiveUserSession,
            connections.IsConnected(device.Id)
                && device.InteractiveUserSession
                && device.LastSeenAt >= now.AddSeconds(-45))).ToArray());
    }

    private static async Task<IResult> CreateEnrollmentAsync(
        Guid organizationId,
        CreateDeviceEnrollmentRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Admin, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Trim().Length > 120)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.DeviceName)] = ["设备名称不能为空且不能超过 120 个字符"]
            });
        }

        var room = await dbContext.LiveRooms.SingleOrDefaultAsync(
            value => value.Id == request.RoomId && value.OrganizationId == organizationId,
            cancellationToken);
        if (room is null)
        {
            return TypedResults.NotFound();
        }

        if (room.DeviceId is not null)
        {
            return TypedResults.Conflict("直播间已经绑定设备，请先解除现有设备后再注册");
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var enrollment = new DeviceEnrollmentEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RoomId = request.RoomId,
            DeviceName = request.DeviceName.Trim(),
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        dbContext.DeviceEnrollments.Add(enrollment);
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new DeviceEnrollmentResponse(enrollment.Id, token, enrollment.ExpiresAt));
    }

    private static async Task<IResult> GetManagementStateAsync(
        Guid organizationId,
        Guid deviceId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        LiveStudio.Cloud.Realtime.DeviceConnectionRegistry connections,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == deviceId && value.OrganizationId == organizationId,
            cancellationToken);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        var capabilityJson = await dbContext.DeviceCapabilities.AsNoTracking()
            .Where(value => value.DeviceId == deviceId && value.OrganizationId == organizationId)
            .Select(value => value.CapabilityJson)
            .SingleOrDefaultAsync(cancellationToken);
        var currentState = await dbContext.CurrentParameterStates.AsNoTracking()
            .Where(value => value.DeviceId == deviceId && value.OrganizationId == organizationId)
            .SingleOrDefaultAsync(cancellationToken);
        var currentPreviewUrls = new Dictionary<ApplicationKind, Uri>();
        if (currentState?.ObsPreviewObjectKey is { } obsPreviewObjectKey)
        {
            currentPreviewUrls[ApplicationKind.Obs] = objectStorage.CreateDownloadUri(
                obsPreviewObjectKey,
                TimeSpan.FromMinutes(5));
        }

        if (currentState?.LiveCompanionPreviewObjectKey is { } liveCompanionPreviewObjectKey)
        {
            currentPreviewUrls[ApplicationKind.LiveCompanion] = objectStorage.CreateDownloadUri(
                liveCompanionPreviewObjectKey,
                TimeSpan.FromMinutes(5));
        }

        var now = DateTimeOffset.UtcNow;
        return TypedResults.Ok(new DeviceManagementState(
            new DeviceSummary(
                device.Id,
                device.RoomId,
                device.Name,
                device.MachineName,
                device.AgentVersion,
                device.OperatingSystem,
                device.LastSeenAt,
                device.InteractiveUserSession,
                connections.IsConnected(device.Id)
                    && device.InteractiveUserSession
                    && device.LastSeenAt >= now.AddSeconds(-45)),
            DeserializeOrNull<DeviceCapability>(capabilityJson),
            DeserializeOrNull<CurrentParameterState>(currentState?.ParametersJson),
            currentPreviewUrls));
    }

    private static async Task<IResult> ListMappingsAsync(
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

        var mappings = await dbContext.DeviceMappings.AsNoTracking()
            .Where(mapping => mapping.OrganizationId == organizationId)
            .OrderBy(mapping => mapping.DeviceId)
            .ThenBy(mapping => mapping.Application)
            .Select(mapping => new DeviceMapping(
                mapping.Id,
                mapping.OrganizationId,
                mapping.DeviceId,
                mapping.SourceLogicalId,
                mapping.Application,
                mapping.TargetDeviceId,
                mapping.TargetSourceName,
                mapping.TargetSceneName,
                mapping.CreateSourceWhenMissing))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(mappings);
    }

    private static async Task<IResult> EnrollAsync(
        EnrollDeviceRequest request,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.EnrollmentToken));
        var enrollment = await dbContext.DeviceEnrollments.SingleOrDefaultAsync(
            value => value.TokenHash == tokenHash,
            cancellationToken);
        if (enrollment is null || enrollment.ConsumedAt is not null || enrollment.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return TypedResults.Unauthorized();
        }

        try
        {
            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(request.PackageSigningPublicKeyPem);
        }
        catch (CryptographicException)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.PackageSigningPublicKeyPem)] = ["存档签名公钥无效"]
            });
        }

        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        var device = new ManagedDeviceEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = enrollment.OrganizationId,
            RoomId = enrollment.RoomId,
            Name = enrollment.DeviceName,
            MachineName = request.MachineName,
            AgentVersion = request.AgentVersion,
            OperatingSystem = request.OperatingSystem,
            ApplicationVersionsJson = "{}",
            CapabilitiesJson = "{}",
            PackageSigningPublicKeyPem = request.PackageSigningPublicKeyPem,
            DeviceKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
            EnrolledAt = now,
            LastSeenAt = now,
            InteractiveUserSession = true
        };
        enrollment.ConsumedAt = now;
        dbContext.Devices.Add(device);
        var room = await dbContext.LiveRooms.SingleAsync(
            value => value.Id == enrollment.RoomId && value.OrganizationId == enrollment.OrganizationId,
            cancellationToken);
        room.DeviceId = device.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new EnrollDeviceResponse(
            device.Id,
            device.OrganizationId,
            device.RoomId,
            secret));
    }

    private static async Task<IResult> HeartbeatAsync(
        Guid deviceId,
        HeartbeatRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var organizationId = GetOrganizationId(user);
        var device = await dbContext.Devices.SingleOrDefaultAsync(
            value => value.Id == deviceId && value.OrganizationId == organizationId,
            cancellationToken);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        device.AgentVersion = request.AgentVersion;
        device.OperatingSystem = request.OperatingSystem;
        device.InteractiveUserSession = request.InteractiveUserSession;
        device.ApplicationVersionsJson = JsonSerializer.Serialize(request.ApplicationVersions);
        if (request.Capabilities is not null)
        {
            device.CapabilitiesJson = JsonSerializer.Serialize(request.Capabilities);
        }

        device.LastSeenAt = DateTimeOffset.UtcNow;
        dbContext.DeviceHeartbeats.Add(new DeviceHeartbeatEntity
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            OrganizationId = device.OrganizationId,
            ObservedAt = device.LastSeenAt,
            InteractiveUserSession = request.InteractiveUserSession,
            AgentVersion = request.AgentVersion,
            ApplicationVersionsJson = device.ApplicationVersionsJson
        });
        if (request.Capabilities is not null)
        {
            var capability = await dbContext.DeviceCapabilities.SingleOrDefaultAsync(
                value => value.DeviceId == deviceId,
                cancellationToken);
            if (capability is null)
            {
                dbContext.DeviceCapabilities.Add(new DeviceCapabilityEntity
                {
                    DeviceId = device.Id,
                    OrganizationId = device.OrganizationId,
                    CapturedAt = device.LastSeenAt,
                    CapabilityJson = device.CapabilitiesJson
                });
            }
            else
            {
                capability.CapturedAt = device.LastSeenAt;
                capability.CapabilityJson = device.CapabilitiesJson;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UpdateCurrentStateAsync(
        Guid deviceId,
        CurrentStateRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId) || request.State.DeviceId != deviceId)
        {
            return TypedResults.Forbid();
        }

        var organizationId = GetOrganizationId(user);
        var device = await dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == deviceId && value.OrganizationId == organizationId,
                cancellationToken);
        if (device is null
            || request.State.RoomId != device.RoomId
            || request.Previews.Count > 2
            || request.Previews.Select(preview => preview.Application).Distinct().Count() != request.Previews.Count
            || request.Previews.Any(preview => request.State.Applications.All(
                application => application.Kind != preview.Application))
            || request.State.Applications.Any(application => application.Kind is not ApplicationKind.Obs and not ApplicationKind.LiveCompanion))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.State)] = ["当前参数状态与设备或直播间不匹配"]
            });
        }

        var previews = new List<(ApplicationKind Application, string MediaType, string Extension, byte[] Content)>();
        foreach (var preview in request.Previews)
        {
            var normalizedMediaType = preview.MediaType.ToLowerInvariant();
            var extension = normalizedMediaType switch
            {
                "image/webp" => ".webp",
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => null
            };
            if (extension is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Previews)] = ["当前画面预览只允许 PNG、WebP 或 JPEG"]
                });
            }

            byte[] content;
            try
            {
                content = Convert.FromBase64String(preview.ContentBase64);
            }
            catch (FormatException)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Previews)] = ["当前画面预览编码无效"]
                });
            }

            if (content.Length is <= 12 or > 4 * 1024 * 1024
                || !HasExpectedImageSignature(normalizedMediaType, content))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Previews)] = ["当前画面预览内容无效"]
                });
            }

            previews.Add((preview.Application, normalizedMediaType, extension, content));
        }

        var entity = await dbContext.CurrentParameterStates.SingleOrDefaultAsync(
            value => value.DeviceId == deviceId && value.OrganizationId == organizationId,
            cancellationToken);
        if (entity is null)
        {
            entity = new CurrentParameterStateEntity
            {
                DeviceId = deviceId,
                OrganizationId = device.OrganizationId,
                RoomId = device.RoomId,
                CapturedAt = request.State.CapturedAt,
                ParameterHash = request.State.ParameterHash,
                ParametersJson = JsonSerializer.Serialize(request.State)
            };
            dbContext.CurrentParameterStates.Add(entity);
        }
        else
        {
            entity.CapturedAt = request.State.CapturedAt;
            entity.ParameterHash = request.State.ParameterHash;
            entity.ParametersJson = JsonSerializer.Serialize(request.State);
        }

        foreach (var preview in previews)
        {
            var objectKey = $"{organizationId:N}/current/{deviceId:N}/{preview.Application.ToString().ToLowerInvariant()}{preview.Extension}";
            await objectStorage.UploadAsync(objectKey, preview.Content, preview.MediaType, cancellationToken);
            if (preview.Application == ApplicationKind.Obs)
            {
                entity.ObsPreviewObjectKey = objectKey;
            }
            else
            {
                entity.LiveCompanionPreviewObjectKey = objectKey;
            }
        }

        if (request.Reason is CurrentStateReason.ManualRefresh
            or CurrentStateReason.RemoteRefresh
            or CurrentStateReason.Restore)
        {
            dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ActorId = $"device:{deviceId:N}",
                Action = request.Reason switch
                {
                    CurrentStateReason.Restore => "current-state.restored",
                    CurrentStateReason.RemoteRefresh => "current-state.remote-refreshed",
                    _ => "current-state.refreshed"
                },
                TargetType = "Device",
                TargetId = deviceId.ToString(),
                OccurredAt = DateTimeOffset.UtcNow,
                DetailJson = JsonSerializer.Serialize(new { previewCount = previews.Count })
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListDeviceMappingsAsync(
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
        var mappings = await dbContext.DeviceMappings.AsNoTracking()
            .Where(mapping => mapping.DeviceId == deviceId && mapping.OrganizationId == organizationId)
            .OrderBy(mapping => mapping.Application)
            .ThenBy(mapping => mapping.TargetSourceName)
            .Select(mapping => new DeviceMapping(
                mapping.Id,
                mapping.OrganizationId,
                mapping.DeviceId,
                mapping.SourceLogicalId,
                mapping.Application,
                mapping.TargetDeviceId,
                mapping.TargetSourceName,
                mapping.TargetSceneName,
                mapping.CreateSourceWhenMissing))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(mappings);
    }

    private static async Task<IResult> SaveMappingAsync(
        Guid organizationId,
        Guid deviceId,
        SaveDeviceMappingRequest request,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Operator, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        if (!await dbContext.Devices.AnyAsync(
                device => device.Id == deviceId && device.OrganizationId == organizationId,
                cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.TargetDeviceId)
            || string.IsNullOrWhiteSpace(request.TargetSourceName)
            || request.TargetDeviceId.Length > 1024
            || request.TargetSourceName.Length > 256
            || request.TargetSceneName is null
            || request.TargetSceneName.Length > 256
            || request.CreateSourceWhenMissing && string.IsNullOrWhiteSpace(request.TargetSceneName))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request)] = ["设备映射字段无效"]
            });
        }

        var capabilityJson = await dbContext.DeviceCapabilities.AsNoTracking()
            .Where(value => value.DeviceId == deviceId && value.OrganizationId == organizationId)
            .Select(value => value.CapabilityJson)
            .SingleOrDefaultAsync(cancellationToken);
        var capability = DeserializeOrNull<DeviceCapability>(capabilityJson);
        if (capability is null
            || !capability.VideoSources.Any(source =>
                source.Application == request.Application
                && string.Equals(source.SourceName, request.TargetSourceName.Trim(), StringComparison.Ordinal)
                && string.Equals(source.Device.InterfaceHint, request.TargetDeviceId.Trim(), StringComparison.Ordinal)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.TargetDeviceId)] = ["目标来源不在 Agent 最近上报的设备能力中"]
            });
        }

        var mapping = await dbContext.DeviceMappings.SingleOrDefaultAsync(
            value => value.OrganizationId == organizationId
                && value.DeviceId == deviceId
                && value.SourceLogicalId == request.SourceLogicalId
                && value.Application == request.Application,
            cancellationToken);
        if (mapping is null)
        {
            mapping = new DeviceMappingEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                DeviceId = deviceId,
                SourceLogicalId = request.SourceLogicalId,
                Application = request.Application,
                TargetDeviceId = request.TargetDeviceId.Trim(),
                TargetSourceName = request.TargetSourceName.Trim(),
                TargetSceneName = request.TargetSceneName.Trim(),
                CreateSourceWhenMissing = request.CreateSourceWhenMissing
            };
            dbContext.DeviceMappings.Add(mapping);
        }
        else
        {
            mapping.TargetDeviceId = request.TargetDeviceId.Trim();
            mapping.TargetSourceName = request.TargetSourceName.Trim();
            mapping.TargetSceneName = request.TargetSceneName.Trim();
            mapping.CreateSourceWhenMissing = request.CreateSourceWhenMissing;
        }

        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            Action = "device-mapping.saved",
            TargetType = "DeviceMapping",
            TargetId = mapping.Id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
            DetailJson = "{}"
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static T? DeserializeOrNull<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool HasExpectedImageSignature(string mediaType, ReadOnlySpan<byte> content) => mediaType switch
    {
        "image/webp" => content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content.Slice(8, 4).SequenceEqual("WEBP"u8),
        "image/png" => content.Length >= 8
            && content[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => content.Length >= 3
            && content[0] == 0xFF
            && content[1] == 0xD8
            && content[2] == 0xFF,
        _ => false
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
