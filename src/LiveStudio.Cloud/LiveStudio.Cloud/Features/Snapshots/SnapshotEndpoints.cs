using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Infrastructure;
using LiveStudio.Cloud.Security;
using LiveStudio.Contracts;
using LiveStudio.Packaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace LiveStudio.Cloud.Features.Snapshots;

public static class SnapshotEndpoints
{
    private const int MultipartPartSize = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Guid, Exception?> LogStagingCleanupFailure = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(2201, nameof(LogStagingCleanupFailure)),
        "无法清理存档上传暂存对象 {UploadId}");
    private static readonly Action<ILogger, string, Exception?> LogMaterializedCleanupFailure = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2202, nameof(LogMaterializedCleanupFailure)),
        "无法清理未提交的存档对象 {ObjectKey}");

    public static IEndpointRouteBuilder MapSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/v1/devices/{deviceId:guid}/snapshot-uploads")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = DeviceAuthenticationHandler.AuthenticationScheme
            });
        devices.MapPost("", CreateUploadAsync);
        devices.MapGet("/{uploadId:guid}/parts/{partNumber:int}", GetUploadPartAsync);
        devices.MapPost("/{uploadId:guid}/complete", CompleteUploadAsync);

        var deviceSnapshots = endpoints.MapGroup("/api/v1/devices/{deviceId:guid}/snapshots")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = DeviceAuthenticationHandler.AuthenticationScheme
            });
        deviceSnapshots.MapGet("/{snapshotId:guid}/download", DownloadForAgentAsync);

        var organizations = endpoints.MapGroup("/api/v1/organizations/{organizationId:guid}")
            .RequireAuthorization();
        organizations.MapGet("/snapshots", ListAsync);
        organizations.MapGet("/snapshots/{snapshotId:guid}", GetDetailAsync);
        organizations.MapGet("/snapshots/{snapshotId:guid}/download", DownloadAsync);
        organizations.MapDelete("/snapshots/{snapshotId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateUploadAsync(
        Guid deviceId,
        CreateSnapshotUploadRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == deviceId
                && value.OrganizationId == GetOrganizationId(user)
                && value.RoomId == request.RoomId,
            cancellationToken);
        if (device is null)
        {
            return TypedResults.NotFound();
        }

        if (!IsSha256(request.Sha256)
            || request.Length <= 0
            || request.Length > 2L * 1024 * 1024 * 1024
            || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Trim().Length > 120)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request)] = ["存档长度或 SHA-256 无效"]
            });
        }

        var uploadId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var objectKey = $"{device.OrganizationId:N}/uploads/{uploadId:N}.lscfg";
        var multipartUploadId = await objectStorage.CreateMultipartUploadAsync(
            objectKey,
            "application/vnd.livestudio.snapshot",
            cancellationToken);
        var upload = new SnapshotUploadEntity
        {
            Id = uploadId,
            OrganizationId = device.OrganizationId,
            RoomId = device.RoomId,
            Name = request.Name.Trim(),
            CreatedBy = $"device:{deviceId:N}",
            ObjectKey = objectKey,
            MultipartUploadId = multipartUploadId,
            ExpectedSha256 = request.Sha256.ToLowerInvariant(),
            ExpectedLength = request.Length,
            ExpiresAt = expiresAt
        };
        try
        {
            dbContext.SnapshotUploads.Add(upload);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await objectStorage.AbortMultipartUploadAsync(
                objectKey,
                multipartUploadId,
                CancellationToken.None);
            throw;
        }

        return TypedResults.Ok(new SnapshotUploadResponse(
            upload.Id,
            MultipartPartSize,
            expiresAt));
    }

    private static async Task<IResult> GetUploadPartAsync(
        Guid deviceId,
        Guid uploadId,
        int partNumber,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var upload = await dbContext.SnapshotUploads.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == uploadId
                && value.OrganizationId == GetOrganizationId(user)
                && value.CreatedBy == $"device:{deviceId:N}",
            cancellationToken);
        var partCount = upload is null
            ? 0
            : checked((int)((upload.ExpectedLength + MultipartPartSize - 1) / MultipartPartSize));
        if (upload is null)
        {
            return TypedResults.NotFound();
        }

        if (upload.CompletedAt is not null
            || upload.ExpiresAt <= DateTimeOffset.UtcNow
            || partNumber < 1
            || partNumber > partCount)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "SnapshotUploadUnavailable",
                detail: "上传会话已完成、已过期或分段编号无效");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        return TypedResults.Ok(new SnapshotUploadPartResponse(
            partNumber,
            objectStorage.CreateUploadPartUri(
                upload.ObjectKey,
                upload.MultipartUploadId,
                partNumber,
                TimeSpan.FromMinutes(5)),
            expiresAt));
    }

    private static async Task<IResult> CompleteUploadAsync(
        Guid deviceId,
        Guid uploadId,
        CompleteSnapshotUploadRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var upload = await dbContext.SnapshotUploads.SingleOrDefaultAsync(
            value => value.Id == uploadId
                && value.OrganizationId == GetOrganizationId(user)
                && value.CreatedBy == $"device:{deviceId:N}",
            cancellationToken);
        if (upload is null)
        {
            return TypedResults.NotFound();
        }

        if (upload.CompletedAt is not null || upload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "SnapshotUploadUnavailable",
                detail: "上传会话已完成或已过期");
        }

        var expectedPartCount = checked((int)(
            (upload.ExpectedLength + MultipartPartSize - 1) / MultipartPartSize));
        if (request.Parts.Count != expectedPartCount
            || request.Parts.Select(part => part.PartNumber).Distinct().Count() != expectedPartCount
            || request.Parts.Any(part => part.PartNumber < 1
                || part.PartNumber > expectedPartCount
                || !IsValidETag(part.ETag)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Parts)] = ["Multipart 分段编号或 ETag 无效"]
            });
        }

        var device = await dbContext.Devices.AsNoTracking().SingleAsync(
            value => value.Id == deviceId && value.OrganizationId == upload.OrganizationId,
            cancellationToken);
        await objectStorage.CompleteMultipartUploadAsync(
            upload.ObjectKey,
            upload.MultipartUploadId,
            request.Parts.Select(part => new CompletedMultipartPart(part.PartNumber, part.ETag)).ToArray(),
            cancellationToken);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"livestudio-{Guid.NewGuid():N}.lscfg");
        var materializedObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        var committed = false;
        try
        {
            var metadata = await objectStorage.GetMetadataAsync(upload.ObjectKey, cancellationToken);
            if (metadata is null || metadata.Length != upload.ExpectedLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["package"] = ["对象存储中的存档不存在或长度不一致"]
                });
            }

            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await objectStorage.DownloadToAsync(upload.ObjectKey, destination, cancellationToken);
            }

            await using (var packageStream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var packageHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(packageStream, cancellationToken));
                if (!string.Equals(packageHash, upload.ExpectedSha256, StringComparison.Ordinal))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["package"] = ["存档 SHA-256 不一致"]
                    });
                }
            }

            var package = await SnapshotPackageReader.ReadAsync(
                temporaryPath,
                keyId => CreateVerificationKey(keyId, deviceId, device.PackageSigningPublicKeyPem),
                cancellationToken);
            if (package.Manifest.OrganizationId != upload.OrganizationId
                || package.Manifest.RoomId != upload.RoomId
                || !string.Equals(package.Manifest.Name, upload.Name, StringComparison.Ordinal))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["manifest"] = ["存档的 Organization、直播间或名称与上传会话不一致"]
                });
            }

            if (await dbContext.Snapshots.AnyAsync(
                    value => value.Id == package.Manifest.SnapshotId
                        && value.OrganizationId == upload.OrganizationId,
                    cancellationToken))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "SnapshotAlreadyExists",
                    detail: "该存档已经存在");
            }

            var finalObjectKey = $"{upload.OrganizationId:N}/snapshots/{package.Manifest.SnapshotId:N}.lscfg";
            await objectStorage.CopyAsync(upload.ObjectKey, finalObjectKey, cancellationToken);
            materializedObjectKeys.Add(finalObjectKey);
            var previewObjectKeys = new Dictionary<ApplicationKind, string>();
            foreach (var preview in package.Snapshot.Previews)
            {
                if (!package.Files.TryGetValue(preview.PackagePath, out var previewFile))
                {
                    throw new SnapshotPackageException($"找不到预览图文件: {preview.PackagePath}");
                }

                var previewKey = $"{upload.OrganizationId:N}/previews/{package.Manifest.SnapshotId:N}/{preview.Application.ToString().ToLowerInvariant()}.webp";
                await objectStorage.UploadAsync(previewKey, previewFile.Content, preview.MediaType, cancellationToken);
                materializedObjectKeys.Add(previewKey);
                previewObjectKeys[preview.Application] = previewKey;
            }

            var now = DateTimeOffset.UtcNow;
            dbContext.Snapshots.Add(new SnapshotEntity
            {
                Id = package.Manifest.SnapshotId,
                OrganizationId = upload.OrganizationId,
                RoomId = upload.RoomId,
                Name = upload.Name,
                CreatedBy = upload.CreatedBy,
                CreatedAt = package.Manifest.CreatedAt,
                PackageObjectKey = finalObjectKey,
                PackageLength = upload.ExpectedLength,
                PackageSha256 = upload.ExpectedSha256,
                ParameterHash = package.Manifest.Files.Single(file => file.Path == "parameters.json").Sha256,
                ManifestJson = JsonSerializer.Serialize(package.Manifest, JsonOptions)
            });
            foreach (var application in package.Snapshot.Applications)
            {
                dbContext.SnapshotComponents.Add(new SnapshotComponentEntity
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = upload.OrganizationId,
                    SnapshotId = package.Manifest.SnapshotId,
                    Application = application.Kind,
                    ApplicationVersion = application.Version,
                    ParametersJson = JsonSerializer.Serialize(application, JsonOptions),
                    PreviewObjectKey = previewObjectKeys.GetValueOrDefault(application.Kind)
                });
            }
            if (package.Snapshot.Assets
                .GroupBy(asset => asset.Sha256, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
            {
                throw new SnapshotPackageException("存档包含重复的滤镜素材引用");
            }

            foreach (var asset in package.Snapshot.Assets)
            {
                if (!package.Files.TryGetValue(asset.PackagePath, out var assetFile)
                    || assetFile.Content.Length != asset.Length
                    || !string.Equals(assetFile.MediaType, asset.MediaType, StringComparison.OrdinalIgnoreCase)
                    || !IsSha256(asset.Sha256)
                    || !string.Equals(
                        Convert.ToHexStringLower(SHA256.HashData(assetFile.Content.Span)),
                        asset.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new SnapshotPackageException($"滤镜素材与参数引用不一致: {asset.PackagePath}");
                }

                var normalizedHash = asset.Sha256.ToLowerInvariant();
                var existingAsset = await dbContext.Assets.FindAsync(
                    [upload.OrganizationId, normalizedHash],
                    cancellationToken);
                if (existingAsset is null)
                {
                    var assetKey = $"{upload.OrganizationId:N}/assets/{normalizedHash}";
                    await objectStorage.UploadAsync(assetKey, assetFile.Content, asset.MediaType, cancellationToken);
                    dbContext.Assets.Add(new AssetEntity
                    {
                        OrganizationId = upload.OrganizationId,
                        Sha256 = normalizedHash,
                        Length = asset.Length,
                        MediaType = asset.MediaType,
                        ObjectKey = assetKey,
                        CreatedAt = now
                    });
                }

                dbContext.SnapshotAssets.Add(new SnapshotAssetEntity
                {
                    OrganizationId = upload.OrganizationId,
                    SnapshotId = package.Manifest.SnapshotId,
                    Sha256 = normalizedHash
                });
            }
            upload.CompletedAt = now;
            var room = await dbContext.LiveRooms.SingleAsync(
                value => value.Id == upload.RoomId && value.OrganizationId == upload.OrganizationId,
                cancellationToken);
            room.LastSnapshotAt = now;
            dbContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = upload.OrganizationId,
                ActorId = upload.CreatedBy,
                Action = "snapshot.created",
                TargetType = "Snapshot",
                TargetId = package.Manifest.SnapshotId.ToString(),
                OccurredAt = now,
                DetailJson = "{}"
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            committed = true;
            return TypedResults.Ok(new CompleteSnapshotUploadResponse(package.Manifest.SnapshotId));
        }
        catch (SnapshotPackageException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["package"] = [exception.Message]
            });
        }
        finally
        {
            if (!committed)
            {
                foreach (var objectKey in materializedObjectKeys)
                {
                    try
                    {
                        await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        LogMaterializedCleanupFailure(
                            loggerFactory.CreateLogger(typeof(SnapshotEndpoints).FullName!),
                            objectKey,
                            exception);
                    }
                }
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            try
            {
                await objectStorage.DeleteAsync(upload.ObjectKey, CancellationToken.None);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                LogStagingCleanupFailure(
                    loggerFactory.CreateLogger(typeof(SnapshotEndpoints).FullName!),
                    upload.Id,
                    exception);
            }
        }
    }

    private static async Task<IResult> ListAsync(
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

        var query = dbContext.Snapshots.AsNoTracking().Where(value => value.OrganizationId == organizationId);
        if (roomId is not null)
        {
            query = query.Where(value => value.RoomId == roomId);
        }

        var snapshots = await query
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => new SnapshotSummary(
                value.Id,
                value.RoomId,
                value.Name,
                value.CreatedAt,
                value.PackageLength,
                value.PackageSha256))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(snapshots);
    }

    private static async Task<IResult> DownloadForAgentAsync(
        Guid deviceId,
        Guid snapshotId,
        ClaimsPrincipal user,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!IsSameDevice(user, deviceId))
        {
            return TypedResults.Forbid();
        }

        var organizationId = GetOrganizationId(user);
        var targetDevice = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(
            device => device.Id == deviceId && device.OrganizationId == organizationId,
            cancellationToken);
        if (targetDevice is null)
        {
            return TypedResults.NotFound();
        }

        var snapshot = await dbContext.Snapshots.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == snapshotId
                && value.OrganizationId == organizationId,
            cancellationToken);
        if (snapshot is null
            || !snapshot.CreatedBy.StartsWith("device:", StringComparison.Ordinal)
            || !Guid.TryParseExact(snapshot.CreatedBy["device:".Length..], "N", out var sourceDeviceId))
        {
            return TypedResults.NotFound();
        }

        var sourceDevice = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(
            device => device.Id == sourceDeviceId && device.OrganizationId == organizationId,
            cancellationToken);
        if (sourceDevice is null)
        {
            return TypedResults.NotFound();
        }

        var lifetime = TimeSpan.FromMinutes(5);
        return TypedResults.Ok(new AgentSnapshotDownloadResponse(
            objectStorage.CreateDownloadUri(snapshot.PackageObjectKey, lifetime),
            DateTimeOffset.UtcNow.Add(lifetime),
            snapshot.PackageSha256,
            snapshot.PackageLength,
            sourceDevice.Id.ToString("N"),
            sourceDevice.PackageSigningPublicKeyPem));
    }

    private static async Task<IResult> GetDetailAsync(
        Guid organizationId,
        Guid snapshotId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var snapshot = await dbContext.Snapshots.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == snapshotId && value.OrganizationId == organizationId,
            cancellationToken);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        var components = await dbContext.SnapshotComponents.AsNoTracking()
            .Where(value => value.SnapshotId == snapshotId && value.OrganizationId == organizationId)
            .OrderBy(value => value.Application)
            .ToListAsync(cancellationToken);
        try
        {
            var applications = components.Select(component =>
                    JsonSerializer.Deserialize<ApplicationSnapshot>(component.ParametersJson, JsonOptions)
                    ?? throw new JsonException($"无法解析 {component.Application} 存档参数"))
                .ToArray();
            var previewUrls = components
                .Where(component => !string.IsNullOrWhiteSpace(component.PreviewObjectKey))
                .ToDictionary(
                    component => component.Application,
                    component => objectStorage.CreateDownloadUri(
                        component.PreviewObjectKey!,
                        TimeSpan.FromMinutes(5)));
            return TypedResults.Ok(new SnapshotDetail(
                new SnapshotSummary(
                    snapshot.Id,
                    snapshot.RoomId,
                    snapshot.Name,
                    snapshot.CreatedAt,
                    snapshot.PackageLength,
                    snapshot.PackageSha256),
                applications,
                previewUrls));
        }
        catch (JsonException exception)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "SnapshotParametersInvalid",
                detail: exception.Message);
        }
    }

    private static async Task<IResult> DownloadAsync(
        Guid organizationId,
        Guid snapshotId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        IObjectStorage objectStorage,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Viewer, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var snapshot = await dbContext.Snapshots.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == snapshotId && value.OrganizationId == organizationId,
            cancellationToken);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        var lifetime = TimeSpan.FromMinutes(5);
        return TypedResults.Ok(new SnapshotDownloadResponse(
            objectStorage.CreateDownloadUri(snapshot.PackageObjectKey, lifetime),
            DateTimeOffset.UtcNow.Add(lifetime)));
    }

    private static async Task<IResult> DeleteAsync(
        Guid organizationId,
        Guid snapshotId,
        ClaimsPrincipal user,
        OrganizationAccessService access,
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await access.HasRoleAsync(user, organizationId, OrganizationRole.Admin, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var snapshot = await dbContext.Snapshots.SingleOrDefaultAsync(
            value => value.Id == snapshotId && value.OrganizationId == organizationId,
            cancellationToken);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        var components = await dbContext.SnapshotComponents
            .Where(value => value.OrganizationId == organizationId && value.SnapshotId == snapshotId)
            .ToListAsync(cancellationToken);
        var snapshotAssets = await dbContext.SnapshotAssets
            .Where(value => value.OrganizationId == organizationId && value.SnapshotId == snapshotId)
            .ToListAsync(cancellationToken);
        var assetHashes = snapshotAssets.Select(value => value.Sha256).ToArray();
        var sharedAssetHashes = await dbContext.SnapshotAssets.AsNoTracking()
            .Where(value => value.OrganizationId == organizationId
                && value.SnapshotId != snapshotId
                && assetHashes.Contains(value.Sha256))
            .Select(value => value.Sha256)
            .Distinct()
            .ToListAsync(cancellationToken);
        var unreferencedHashes = assetHashes.Except(sharedAssetHashes, StringComparer.Ordinal).ToArray();
        var unreferencedAssets = await dbContext.Assets
            .Where(value => value.OrganizationId == organizationId
                && unreferencedHashes.Contains(value.Sha256))
            .ToListAsync(cancellationToken);
        var objectKeys = components
            .Select(value => value.PreviewObjectKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Append(snapshot.PackageObjectKey)
            .Concat(unreferencedAssets.Select(value => value.ObjectKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        dbContext.SnapshotAssets.RemoveRange(snapshotAssets);
        dbContext.Assets.RemoveRange(unreferencedAssets);
        dbContext.Snapshots.Remove(snapshot);
        dbContext.ObjectDeletions.AddRange(objectKeys.Select(objectKey => new ObjectDeletionEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ObjectKey = objectKey,
            CreatedAt = now,
            NextAttemptAt = now
        }));
        var room = await dbContext.LiveRooms.SingleAsync(
            value => value.Id == snapshot.RoomId && value.OrganizationId == organizationId,
            cancellationToken);
        room.LastSnapshotAt = await dbContext.Snapshots.AsNoTracking()
            .Where(value => value.OrganizationId == organizationId
                && value.RoomId == snapshot.RoomId
                && value.Id != snapshotId)
            .MaxAsync(value => (DateTimeOffset?)value.CreatedAt, cancellationToken);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            Action = "snapshot.deleted",
            TargetType = "Snapshot",
            TargetId = snapshot.Id.ToString(),
            OccurredAt = now,
            DetailJson = JsonSerializer.Serialize(new
            {
                queuedObjectCount = objectKeys.Length,
                removedAssetCount = unreferencedAssets.Count
            })
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ECDsa? CreateVerificationKey(string keyId, Guid deviceId, string publicKeyPem)
    {
        if (!string.Equals(keyId, deviceId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key;
    }

    private static bool IsSameDevice(ClaimsPrincipal user, Guid deviceId) => string.Equals(
        user.FindFirstValue(DeviceAuthenticationHandler.DeviceIdClaim),
        deviceId.ToString(),
        StringComparison.OrdinalIgnoreCase);

    private static Guid GetOrganizationId(ClaimsPrincipal user) => Guid.TryParse(
        user.FindFirstValue(DeviceAuthenticationHandler.OrganizationIdClaim),
        out var organizationId)
        ? organizationId
        : Guid.Empty;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsValidETag(string value)
    {
        var normalized = value.Trim();
        return normalized.Length is >= 3 and <= 130
            && normalized[0] == '"'
            && normalized[^1] == '"'
            && normalized[1..^1].All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }
}
