using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LiveStudio.Cloud.Data;
using LiveStudio.Cloud.Infrastructure;
using LiveStudio.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveStudio.Cloud.IntegrationTests;

public sealed class CloudIntegrationTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly CloudApplicationFactory _factory = new();

    public async Task InitializeAsync()
    {
        _ = _factory.CreateClient();
        await _factory.ResetDomainDataAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IdentityStoreCreatesAndValidatesUser()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = "integration@livestudio.test",
            Email = "integration@livestudio.test",
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, "Integration-Password-2026!");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        Assert.True(await userManager.CheckPasswordAsync(user, "Integration-Password-2026!"));
        Assert.False(await userManager.CheckPasswordAsync(user, "wrong-password"));
    }

    [Fact]
    public async Task DesktopAuthorizationEnforcesOrganizationBoundaryAndApiStatusCodes()
    {
        var seeded = await SeedAuthorizedDesktopAsync();
        using var client = CreateClient(allowAutoRedirect: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seeded.Token);

        var ownRooms = await client.GetAsync($"/api/v1/organizations/{seeded.OrganizationId}/rooms");
        var foreignRooms = await client.GetAsync($"/api/v1/organizations/{seeded.ForeignOrganizationId}/rooms");
        var ownAudit = await client.GetAsync($"/api/v1/organizations/{seeded.OrganizationId}/audit-events");

        Assert.Equal(HttpStatusCode.OK, ownRooms.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRooms.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownAudit.StatusCode);

        using var anonymousClient = CreateClient(allowAutoRedirect: false);
        var anonymous = await anonymousClient.GetAsync($"/api/v1/organizations/{seeded.OrganizationId}/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task EnrollmentCreatesDeviceAndHeartbeatRejectsWrongDevicePath()
    {
        var seeded = await SeedAuthorizedDesktopAsync();
        using var desktopClient = CreateClient(allowAutoRedirect: false);
        desktopClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seeded.Token);
        var enrollmentResponse = await desktopClient.PostAsJsonAsync(
            $"/api/v1/organizations/{seeded.OrganizationId}/device-enrollments",
            new CreateDeviceEnrollmentRequest(seeded.RoomId, "Integration Agent"));
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>();
        Assert.NotNull(enrollment);

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrollResponse = await desktopClient.PostAsJsonAsync(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(
                enrollment.EnrollmentToken,
                "INTEGRATION-PC",
                "1.0.0",
                "Windows 11",
                signingKey.ExportSubjectPublicKeyInfoPem()));
        enrollResponse.EnsureSuccessStatusCode();
        var enrolled = await enrollResponse.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        Assert.NotNull(enrolled);

        using var deviceClient = CreateClient(allowAutoRedirect: false);
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Device",
            $"{enrolled.DeviceId}.{enrolled.DeviceSecret}");
        var heartbeat = new HeartbeatRequest(
            "1.1.0",
            "Windows 11 24H2",
            true,
            new Dictionary<ApplicationKind, string>
            {
                [ApplicationKind.Obs] = "31.1.2",
                [ApplicationKind.LiveCompanion] = "8.2.0"
            },
            null);

        var accepted = await deviceClient.PostAsJsonAsync(
            $"/api/v1/devices/{enrolled.DeviceId}/heartbeat",
            heartbeat);
        var rejected = await deviceClient.PostAsJsonAsync(
            $"/api/v1/devices/{Guid.NewGuid()}/heartbeat",
            heartbeat);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.True(
            rejected.StatusCode == HttpStatusCode.Forbidden,
            $"错误设备路径应返回 403，实际为 {(int)rejected.StatusCode}：{await rejected.Content.ReadAsStringAsync()}");
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await dbContext.Devices.AsNoTracking().SingleAsync(value => value.Id == enrolled.DeviceId);
        Assert.Equal("1.1.0", stored.AgentVersion);
        Assert.True(stored.InteractiveUserSession);
        Assert.Equal(1, await dbContext.DeviceHeartbeats.CountAsync(value => value.DeviceId == enrolled.DeviceId));
    }

    [Fact]
    public async Task ObjectStorageCompletesMultipartAndPreservesContent()
    {
        var content = RandomNumberGenerator.GetBytes(11 * 1024 * 1024 + 137);
        var objectKey = $"integration/{Guid.NewGuid():N}.bin";
        await using var scope = _factory.Services.CreateAsyncScope();
        var objectStorage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var uploadId = await objectStorage.CreateMultipartUploadAsync(
            objectKey,
            "application/octet-stream",
            CancellationToken.None);

        try
        {
            using var httpClient = new HttpClient();
            var parts = new List<CompletedMultipartPart>();
            const int partSize = 8 * 1024 * 1024;
            for (var offset = 0; offset < content.Length; offset += partSize)
            {
                var partNumber = offset / partSize + 1;
                var length = Math.Min(partSize, content.Length - offset);
                using var partContent = new ByteArrayContent(content, offset, length);
                using var response = await httpClient.PutAsync(
                    objectStorage.CreateUploadPartUri(objectKey, uploadId, partNumber, TimeSpan.FromMinutes(2)),
                    partContent,
                    CancellationToken.None);
                response.EnsureSuccessStatusCode();
                Assert.NotNull(response.Headers.ETag);
                parts.Add(new CompletedMultipartPart(partNumber, response.Headers.ETag.Tag));
            }

            await objectStorage.CompleteMultipartUploadAsync(
                objectKey,
                uploadId,
                parts,
                CancellationToken.None);
            await using var downloaded = new MemoryStream();
            await objectStorage.DownloadToAsync(objectKey, downloaded, CancellationToken.None);

            Assert.Equal(content.Length, downloaded.Length);
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(downloaded.ToArray()));
        }
        finally
        {
            await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SnapshotDeletionKeepsSharedAssetsAndDeletesUnreferencedObjects()
    {
        var seeded = await SeedAuthorizedDesktopAsync();
        var targetSnapshotId = Guid.NewGuid();
        var retainedSnapshotId = Guid.NewGuid();
        var sharedHash = Convert.ToHexStringLower(SHA256.HashData("shared"u8));
        var uniqueHash = Convert.ToHexStringLower(SHA256.HashData("unique"u8));
        var prefix = $"{seeded.OrganizationId:N}/integration/{Guid.NewGuid():N}";
        var targetPackageKey = $"{prefix}/target.lscfg";
        var retainedPackageKey = $"{prefix}/retained.lscfg";
        var previewKey = $"{prefix}/preview.webp";
        var sharedAssetKey = $"{prefix}/shared.asset";
        var uniqueAssetKey = $"{prefix}/unique.asset";
        var keys = new[]
        {
            targetPackageKey,
            retainedPackageKey,
            previewKey,
            sharedAssetKey,
            uniqueAssetKey
        };
        await using var seedScope = _factory.Services.CreateAsyncScope();
        var objectStorage = seedScope.ServiceProvider.GetRequiredService<IObjectStorage>();
        foreach (var key in keys)
        {
            await objectStorage.UploadAsync(key, "integration"u8.ToArray(), "application/octet-stream", CancellationToken.None);
        }

        try
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTimeOffset.UtcNow;
            dbContext.Snapshots.AddRange(
                CreateSnapshot(targetSnapshotId, seeded, targetPackageKey, now),
                CreateSnapshot(retainedSnapshotId, seeded, retainedPackageKey, now.AddMinutes(-1)));
            dbContext.SnapshotComponents.Add(new SnapshotComponentEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = seeded.OrganizationId,
                SnapshotId = targetSnapshotId,
                Application = ApplicationKind.Obs,
                ApplicationVersion = "31.1.2",
                ParametersJson = "{}",
                PreviewObjectKey = previewKey
            });
            dbContext.Assets.AddRange(
                new AssetEntity
                {
                    OrganizationId = seeded.OrganizationId,
                    Sha256 = sharedHash,
                    Length = 11,
                    MediaType = "application/octet-stream",
                    ObjectKey = sharedAssetKey,
                    CreatedAt = now
                },
                new AssetEntity
                {
                    OrganizationId = seeded.OrganizationId,
                    Sha256 = uniqueHash,
                    Length = 11,
                    MediaType = "application/octet-stream",
                    ObjectKey = uniqueAssetKey,
                    CreatedAt = now
                });
            dbContext.SnapshotAssets.AddRange(
                new SnapshotAssetEntity
                {
                    OrganizationId = seeded.OrganizationId,
                    SnapshotId = targetSnapshotId,
                    Sha256 = sharedHash
                },
                new SnapshotAssetEntity
                {
                    OrganizationId = seeded.OrganizationId,
                    SnapshotId = targetSnapshotId,
                    Sha256 = uniqueHash
                },
                new SnapshotAssetEntity
                {
                    OrganizationId = seeded.OrganizationId,
                    SnapshotId = retainedSnapshotId,
                    Sha256 = sharedHash
                });
            await dbContext.SaveChangesAsync();

            using var client = CreateClient(allowAutoRedirect: false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seeded.Token);
            var response = await client.DeleteAsync(
                $"/api/v1/organizations/{seeded.OrganizationId}/snapshots/{targetSnapshotId}");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            await using var verificationScope = _factory.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verificationDb.Snapshots.AnyAsync(value => value.Id == targetSnapshotId));
            Assert.True(await verificationDb.Snapshots.AnyAsync(value => value.Id == retainedSnapshotId));
            Assert.True(await verificationDb.Assets.AnyAsync(value => value.Sha256 == sharedHash));
            Assert.False(await verificationDb.Assets.AnyAsync(value => value.Sha256 == uniqueHash));
            Assert.Equal(3, await verificationDb.ObjectDeletions.CountAsync());

            var deletionWorker = _factory.Services.GetRequiredService<ObjectDeletionWorker>();
            Assert.Equal(3, await deletionWorker.ProcessPendingAsync(CancellationToken.None));
            Assert.Null(await objectStorage.GetMetadataAsync(targetPackageKey, CancellationToken.None));
            Assert.Null(await objectStorage.GetMetadataAsync(previewKey, CancellationToken.None));
            Assert.Null(await objectStorage.GetMetadataAsync(uniqueAssetKey, CancellationToken.None));
            Assert.NotNull(await objectStorage.GetMetadataAsync(sharedAssetKey, CancellationToken.None));
        }
        finally
        {
            foreach (var key in keys)
            {
                await objectStorage.DeleteAsync(key, CancellationToken.None);
            }
        }
    }

    private async Task<SeededDesktop> SeedAuthorizedDesktopAsync()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var organizationId = Guid.NewGuid();
        var foreignOrganizationId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"desktop-{Guid.NewGuid():N}@livestudio.test",
            Email = $"desktop-{Guid.NewGuid():N}@livestudio.test",
            EmailConfirmed = true
        };
        var identityResult = await userManager.CreateAsync(user, "Integration-Password-2026!");
        Assert.True(identityResult.Succeeded, string.Join("; ", identityResult.Errors.Select(error => error.Description)));

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Organizations.AddRange(
            new OrganizationEntity { Id = organizationId, Name = "Primary", CreatedAt = DateTimeOffset.UtcNow },
            new OrganizationEntity { Id = foreignOrganizationId, Name = "Foreign", CreatedAt = DateTimeOffset.UtcNow });
        dbContext.OrganizationMembers.Add(new OrganizationMemberEntity
        {
            OrganizationId = organizationId,
            UserId = user.Id,
            Role = OrganizationRole.Owner
        });
        dbContext.LiveRooms.Add(new LiveRoomEntity
        {
            Id = roomId,
            OrganizationId = organizationId,
            Name = "Integration Room"
        });
        dbContext.DesktopAccessTokens.Add(new DesktopAccessTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = "Integration Desktop",
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await dbContext.SaveChangesAsync();
        return new SeededDesktop(token, organizationId, foreignOrganizationId, roomId);
    }

    private HttpClient CreateClient(bool allowAutoRedirect)
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect,
            BaseAddress = new Uri("https://localhost")
        });
    }

    private static SnapshotEntity CreateSnapshot(
        Guid snapshotId,
        SeededDesktop seeded,
        string packageObjectKey,
        DateTimeOffset createdAt)
    {
        return new SnapshotEntity
        {
            Id = snapshotId,
            OrganizationId = seeded.OrganizationId,
            RoomId = seeded.RoomId,
            Name = $"Snapshot {snapshotId:N}",
            CreatedBy = "integration",
            CreatedAt = createdAt,
            PackageObjectKey = packageObjectKey,
            PackageLength = 11,
            PackageSha256 = new string('0', 64),
            ParameterHash = new string('1', 64),
            ManifestJson = "{}"
        };
    }

    private sealed record SeededDesktop(
        string Token,
        Guid OrganizationId,
        Guid ForeignOrganizationId,
        Guid RoomId);
}
