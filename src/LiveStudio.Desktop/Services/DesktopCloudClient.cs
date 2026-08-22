using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.Services;

public sealed record DesktopCloudWorkspace(
    IReadOnlyList<OrganizationSummary> Organizations,
    OrganizationSummary? SelectedOrganization,
    IReadOnlyList<LiveRoomSummary> Rooms,
    IReadOnlyList<DeviceSummary> Devices,
    IReadOnlyList<SnapshotSummary> Snapshots,
    IReadOnlyList<JobSummary> Jobs);

public sealed class DesktopCloudClient(
    IDesktopCredentialStore credentialStore,
    HttpMessageHandler? messageHandler = null)
{
    private readonly HttpMessageHandler? messageHandler = messageHandler;

    public bool TryLoadCredentials([NotNullWhen(true)] out DesktopCloudCredentials? credentials)
    {
        if (!credentialStore.TryLoad(out credentials))
        {
            return false;
        }

        if (credentials.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            credentialStore.Delete();
            credentials = null;
            return false;
        }

        return true;
    }

    public async Task<StartDesktopAuthorizationResponse> StartAuthorizationAsync(
        Uri serviceUri,
        string deviceName,
        CancellationToken cancellationToken)
    {
        ValidateServiceUri(serviceUri);
        using var client = CreateClient(serviceUri, null);
        using var response = await client.PostAsJsonAsync(
            "api/v1/desktop-auth/sessions",
            new StartDesktopAuthorizationRequest(deviceName),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartDesktopAuthorizationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("云端未返回桌面授权信息");
    }

    public async Task<PollDesktopAuthorizationResponse> PollAuthorizationAsync(
        Uri serviceUri,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(serviceUri, null);
        using var response = await client.PostAsJsonAsync(
            "api/v1/desktop-auth/sessions/poll",
            new PollDesktopAuthorizationRequest(deviceCode),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PollDesktopAuthorizationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("云端未返回桌面授权状态");
    }

    public void SaveCredentials(
        Uri serviceUri,
        string accessToken,
        DateTimeOffset expiresAt,
        Guid? selectedOrganizationId = null) =>
        credentialStore.Save(new DesktopCloudCredentials(
            serviceUri,
            accessToken,
            expiresAt,
            selectedOrganizationId));

    public void SaveCredentials(DesktopCloudCredentials credentials) => credentialStore.Save(credentials);

    public void ForgetCredentials() => credentialStore.Delete();

    public async Task<DesktopCloudWorkspace> LoadWorkspaceAsync(
        DesktopCloudCredentials credentials,
        Guid? selectedOrganizationId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        var organizations = await GetAsync<IReadOnlyList<OrganizationSummary>>(
            client,
            "api/v1/organizations/",
            cancellationToken) ?? [];
        var selectedOrganization = organizations.FirstOrDefault(value => value.Id == selectedOrganizationId)
            ?? (organizations.Count > 0 ? organizations[0] : null);
        if (selectedOrganization is null)
        {
            return new DesktopCloudWorkspace(organizations, null, [], [], [], []);
        }

        var basePath = $"api/v1/organizations/{selectedOrganization.Id}";
        var roomsTask = GetAsync<IReadOnlyList<LiveRoomSummary>>(client, $"{basePath}/rooms", cancellationToken);
        var devicesTask = GetAsync<IReadOnlyList<DeviceSummary>>(client, $"{basePath}/devices", cancellationToken);
        var snapshotsTask = GetAsync<IReadOnlyList<SnapshotSummary>>(client, $"{basePath}/snapshots", cancellationToken);
        var jobsTask = GetAsync<IReadOnlyList<JobSummary>>(client, $"{basePath}/jobs", cancellationToken);
        await Task.WhenAll(roomsTask, devicesTask, snapshotsTask, jobsTask);
        return new DesktopCloudWorkspace(
            organizations,
            selectedOrganization,
            await roomsTask ?? [],
            await devicesTask ?? [],
            await snapshotsTask ?? [],
            await jobsTask ?? []);
    }

    public async Task<DeviceManagementState> GetManagementStateAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        return await GetAsync<DeviceManagementState>(
                client,
                $"api/v1/organizations/{organizationId}/devices/{deviceId}/management-state",
                cancellationToken)
            ?? throw new InvalidOperationException("云端没有返回设备状态");
    }

    public async Task<DeviceEnrollmentResponse> CreateDeviceEnrollmentAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        CreateDeviceEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/organizations/{organizationId}/device-enrollments",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>(cancellationToken)
            ?? throw new InvalidOperationException("云端没有返回设备注册码");
    }

    public async Task<IReadOnlyList<DeviceMapping>> GetMappingsAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        return await GetAsync<IReadOnlyList<DeviceMapping>>(
                client,
                $"api/v1/organizations/{organizationId}/device-mappings",
                cancellationToken) ?? [];
    }

    public async Task SaveMappingAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        Guid deviceId,
        SaveDeviceMappingRequest request,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        using var response = await client.PutAsJsonAsync(
            $"api/v1/organizations/{organizationId}/devices/{deviceId}/mappings",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SnapshotDetail> GetSnapshotDetailAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        return await GetAsync<SnapshotDetail>(
                client,
                $"api/v1/organizations/{organizationId}/snapshots/{snapshotId}",
                cancellationToken)
            ?? throw new InvalidOperationException("云端没有返回存档详情");
    }

    public async Task<byte[]> DownloadPreviewAsync(Uri previewUri, CancellationToken cancellationToken)
    {
        using var client = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(20);
        return await client.GetByteArrayAsync(previewUri, cancellationToken);
    }

    public async Task CreateCaptureJobAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        CreateCaptureJobRequest request,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/organizations/{organizationId}/capture-jobs",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateRestoreJobAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        CreateRestoreJobRequest request,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/organizations/{organizationId}/restore-jobs",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateRefreshPreviewJobAsync(
        DesktopCloudCredentials credentials,
        Guid organizationId,
        CreateRefreshPreviewJobRequest request,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
        using var response = await client.PostAsJsonAsync(
            $"api/v1/organizations/{organizationId}/refresh-jobs",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeAsync(
        DesktopCloudCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(credentials.ServiceUri, credentials.AccessToken);
            using var response = await client.DeleteAsync("api/v1/desktop-auth/tokens/current", cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            credentialStore.Delete();
        }
    }

    private HttpClient CreateClient(Uri serviceUri, string? accessToken)
    {
        var client = messageHandler is null
            ? new HttpClient()
            : new HttpClient(messageHandler, disposeHandler: false);
        client.BaseAddress = NormalizeBaseUri(serviceUri);
        client.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    private static async Task<T?> GetAsync<T>(
        HttpClient client,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static Uri NormalizeBaseUri(Uri serviceUri)
    {
        ValidateServiceUri(serviceUri);
        var builder = new UriBuilder(serviceUri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static void ValidateServiceUri(Uri serviceUri)
    {
        if (!serviceUri.IsAbsoluteUri
            || serviceUri.Scheme != Uri.UriSchemeHttps
                && !(serviceUri.Scheme == Uri.UriSchemeHttp && serviceUri.IsLoopback))
        {
            throw new ArgumentException("云端服务必须使用 HTTPS；本机开发允许 http://localhost", nameof(serviceUri));
        }
    }
}
