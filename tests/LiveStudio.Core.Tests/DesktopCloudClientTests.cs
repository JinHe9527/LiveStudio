using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class DesktopCloudClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StartAuthorizationUsesAnonymousSessionEndpoint()
    {
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/desktop-auth/sessions", request.RequestUri?.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Json(new StartDesktopAuthorizationResponse(
                "device-code",
                "ABCD-EFGH",
                new Uri("https://studio.example/desktop-authorize?userCode=ABCDEFGH"),
                DateTimeOffset.UtcNow.AddMinutes(10),
                3));
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);

        var result = await client.StartAuthorizationAsync(
            new Uri("https://studio.example"),
            "Mac Studio",
            CancellationToken.None);

        Assert.Equal("ABCD-EFGH", result.UserCode);
    }

    [Fact]
    public async Task WorkspaceRequestsCarryDesktopBearerToken()
    {
        var organizationId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("请求缺少 URI");
            return path switch
            {
                "/api/v1/organizations/" => Json<IReadOnlyList<OrganizationSummary>>(
                    [new OrganizationSummary(organizationId, "Studio")]),
                var value when value.EndsWith("/rooms", StringComparison.Ordinal) => Json<IReadOnlyList<LiveRoomSummary>>([]),
                var value when value.EndsWith("/devices", StringComparison.Ordinal) => Json<IReadOnlyList<DeviceSummary>>([]),
                var value when value.EndsWith("/snapshots", StringComparison.Ordinal) => Json<IReadOnlyList<SnapshotSummary>>([]),
                var value when value.EndsWith("/jobs", StringComparison.Ordinal) => Json<IReadOnlyList<JobSummary>>([]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1));

        var workspace = await client.LoadWorkspaceAsync(credentials, null, CancellationToken.None);

        Assert.Equal(organizationId, workspace.SelectedOrganization?.Id);
    }

    [Fact]
    public async Task NonTlsRemoteServiceIsRejectedBeforeNetworkRequest()
    {
        var handler = new RouteHandler(_ => throw new InvalidOperationException("不应发起网络请求"));
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAuthorizationAsync(
            new Uri("http://studio.example"),
            "Windows PC",
            CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceUsesRequestedOrganization()
    {
        var firstOrganizationId = Guid.NewGuid();
        var selectedOrganizationId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("请求缺少 URI");
            return path switch
            {
                "/api/v1/organizations/" => Json<IReadOnlyList<OrganizationSummary>>(
                    [
                        new OrganizationSummary(firstOrganizationId, "First"),
                        new OrganizationSummary(selectedOrganizationId, "Selected")
                    ]),
                var value when value.Contains(selectedOrganizationId.ToString(), StringComparison.Ordinal)
                    => value.EndsWith("/rooms", StringComparison.Ordinal)
                        ? Json<IReadOnlyList<LiveRoomSummary>>([])
                        : value.EndsWith("/devices", StringComparison.Ordinal)
                            ? Json<IReadOnlyList<DeviceSummary>>([])
                            : value.EndsWith("/snapshots", StringComparison.Ordinal)
                                ? Json<IReadOnlyList<SnapshotSummary>>([])
                                : value.EndsWith("/jobs", StringComparison.Ordinal)
                                    ? Json<IReadOnlyList<JobSummary>>([])
                                    : new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            selectedOrganizationId);

        var workspace = await client.LoadWorkspaceAsync(
            credentials,
            credentials.SelectedOrganizationId,
            CancellationToken.None);

        Assert.Equal(selectedOrganizationId, workspace.SelectedOrganization?.Id);
    }

    [Fact]
    public async Task RevokeClearsLocalCredentialWhenServerIsUnavailable()
    {
        var store = new MemoryCredentialStore();
        var client = new DesktopCloudClient(
            store,
            new RouteHandler(_ => throw new HttpRequestException("offline")));
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1));
        client.SaveCredentials(credentials);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RevokeAsync(
            credentials,
            CancellationToken.None));

        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public async Task DeviceEnrollmentUsesSelectedOrganizationAndDesktopToken()
    {
        var organizationId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"/api/v1/organizations/{organizationId}/device-enrollments",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            return Json(new DeviceEnrollmentResponse(Guid.NewGuid(), new string('b', 43), expiresAt));
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);

        var result = await client.CreateDeviceEnrollmentAsync(
            credentials,
            organizationId,
            new CreateDeviceEnrollmentRequest(roomId, "直播电脑 A"),
            CancellationToken.None);

        Assert.Equal(expiresAt, result.ExpiresAt);
    }

    [Fact]
    public async Task CreateRoomUsesSelectedOrganizationAndReturnsNewRoom()
    {
        var organizationId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"/api/v1/organizations/{organizationId}/rooms",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            var body = request.Content!.ReadFromJsonAsync<CreateRoomRequest>().GetAwaiter().GetResult();
            Assert.Equal("3号直播间", body?.Name);
            return Json(new LiveRoomSummary(
                roomId,
                organizationId,
                "3号直播间",
                null,
                null,
                false,
                false));
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);

        var result = await client.CreateRoomAsync(
            credentials,
            organizationId,
            " 3号直播间 ",
            CancellationToken.None);

        Assert.Equal(roomId, result.Id);
        Assert.Equal("3号直播间", result.Name);
    }

    [Fact]
    public async Task BatchCapturePostsSelectedRoomsAndReturnsPerRoomResults()
    {
        var organizationId = Guid.NewGuid();
        var acceptedRoomId = Guid.NewGuid();
        var rejectedRoomId = Guid.NewGuid();
        var acceptedJobId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"/api/v1/organizations/{organizationId}/capture-jobs/batch",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            var body = request.Content!.ReadFromJsonAsync<CreateBatchCaptureJobsRequest>().GetAwaiter().GetResult();
            Assert.NotNull(body);
            Assert.Equal([acceptedRoomId, rejectedRoomId], body.RoomIds);
            return Json(new CreateBatchCaptureJobsResponse(
                2,
                1,
                1,
                [
                    new BatchCaptureJobResult(
                        acceptedRoomId, "A", Guid.NewGuid(), acceptedJobId, true, "Accepted", "保存任务已下发"),
                    new BatchCaptureJobResult(
                        rejectedRoomId, "B", null, null, false, "DeviceNotAssigned", "尚未绑定 Windows 执行端")
                ]));
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);

        var result = await client.CreateBatchCaptureJobsAsync(
            credentials,
            organizationId,
            new CreateBatchCaptureJobsRequest([acceptedRoomId, rejectedRoomId], "统一存档"),
            CancellationToken.None);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(acceptedJobId, result.Results.Single(item => item.Accepted).JobId);
        Assert.Equal("DeviceNotAssigned", result.Results.Single(item => !item.Accepted).Code);
    }

    [Fact]
    public async Task RenameCloudSnapshotUsesOrganizationScopedEndpoint()
    {
        var organizationId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(
                $"/api/v1/organizations/{organizationId}/snapshots/{snapshotId}/name",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            var body = request.Content!.ReadFromJsonAsync<RenameCloudSnapshotRequest>().GetAwaiter().GetResult();
            Assert.Equal("主机位晚场", body?.Name);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);

        await client.RenameSnapshotAsync(
            credentials,
            organizationId,
            snapshotId,
            " 主机位晚场 ",
            CancellationToken.None);
    }

    [Fact]
    public async Task DeleteCloudSnapshotUsesOrganizationScopedEndpoint()
    {
        var organizationId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var handler = new RouteHandler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal(
                $"/api/v1/organizations/{organizationId}/snapshots/{snapshotId}",
                request.RequestUri?.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);

        await client.DeleteSnapshotAsync(
            credentials,
            organizationId,
            snapshotId,
            CancellationToken.None);
    }

    [Fact]
    public async Task DownloadSnapshotVerifiesPublicObjectLengthAndHash()
    {
        var organizationId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var package = Encoding.UTF8.GetBytes("verified-cloud-snapshot");
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(package));
        var downloadUri = new Uri("https://objects.example/livestudio/package.lscfg?signature=test");
        var handler = new RouteHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == $"/api/v1/organizations/{organizationId}/snapshots/{snapshotId}/download")
            {
                Assert.Equal(new AuthenticationHeaderValue("Bearer", "desktop-token"), request.Headers.Authorization);
                return Json(new SnapshotDownloadResponse(downloadUri, DateTimeOffset.UtcNow.AddMinutes(5)));
            }

            Assert.Equal(downloadUri, request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package)
            };
        });
        var client = new DesktopCloudClient(new MemoryCredentialStore(), handler);
        var credentials = new DesktopCloudCredentials(
            new Uri("https://studio.example"),
            "desktop-token",
            DateTimeOffset.UtcNow.AddDays(1),
            organizationId);
        var snapshot = new SnapshotSummary(
            snapshotId,
            Guid.NewGuid(),
            "云端存档",
            DateTimeOffset.UtcNow,
            package.LongLength,
            packageSha256);
        var destination = Path.Combine(Path.GetTempPath(), $"livestudio-cloud-download-{Guid.NewGuid():N}.lscfg");
        try
        {
            await client.DownloadSnapshotAsync(
                credentials,
                organizationId,
                snapshot,
                destination,
                CancellationToken.None);

            Assert.Equal(package, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, JsonOptions),
            Encoding.UTF8,
            "application/json")
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class MemoryCredentialStore : IDesktopCredentialStore
    {
        private DesktopCloudCredentials? credentials;

        public void Save(DesktopCloudCredentials value) => credentials = value;

        public bool TryLoad([NotNullWhen(true)] out DesktopCloudCredentials? value)
        {
            value = credentials;
            return value is not null;
        }

        public void Delete() => credentials = null;
    }
}
