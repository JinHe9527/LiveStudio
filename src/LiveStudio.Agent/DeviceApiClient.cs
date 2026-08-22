using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed record DownloadedSnapshotPackage(
    string PackagePath,
    string SigningKeyId,
    string SigningPublicKeyPem);

public sealed class DeviceApiClient : IDisposable
{
    private readonly DeviceCredentials credentials;
    private readonly HttpClient httpClient;

    public DeviceApiClient(IDeviceCredentialStore credentialStore)
    {
        credentials = credentialStore.Load();
        httpClient = CreateHttpClient(credentials);
    }

    public DeviceCredentials Credentials => credentials;

    public async Task SendHeartbeatAsync(HeartbeatRequest heartbeat, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/devices/{credentials.DeviceId}/heartbeat",
            heartbeat,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateCurrentStateAsync(
        CurrentParameterState state,
        IReadOnlyList<CurrentPreviewUpload> previews,
        CurrentStateReason reason,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"/api/v1/devices/{credentials.DeviceId}/current-state",
            new CurrentStateRequest(state, previews, reason),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<AgentJobNotification>> GetAvailableJobsAsync(CancellationToken cancellationToken) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<AgentJobNotification>>(
            $"/api/v1/devices/{credentials.DeviceId}/jobs/available",
            cancellationToken) ?? [];

    public async Task<ClaimJobResponse?> ClaimAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            $"/api/v1/devices/{credentials.DeviceId}/jobs/{jobId}/claim",
            null,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimJobResponse>(cancellationToken);
    }

    public async Task ReportAsync(
        Guid jobId,
        JobStatus status,
        string message,
        string? detailCode,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/devices/{credentials.DeviceId}/jobs/{jobId}/events",
            new ReportJobEventRequest(status, message, detailCode),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<DeviceMapping>> GetMappingsAsync(CancellationToken cancellationToken) =>
        await httpClient.GetFromJsonAsync<IReadOnlyList<DeviceMapping>>(
            $"/api/v1/devices/{credentials.DeviceId}/mappings",
            cancellationToken) ?? [];

    public async Task<DownloadedSnapshotPackage> DownloadSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var download = await httpClient.GetFromJsonAsync<AgentSnapshotDownloadResponse>(
            $"/api/v1/devices/{credentials.DeviceId}/snapshots/{snapshotId}/download",
            cancellationToken) ?? throw new InvalidOperationException("云端没有返回存档下载信息");
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Downloads");
        Directory.CreateDirectory(directory);
        var packagePath = Path.Combine(directory, $"{snapshotId:N}.lscfg");
        var partialPath = $"{packagePath}.partial-{Guid.NewGuid():N}";
        try
        {
            using var downloadClient = new HttpClient();
            await using (var source = await downloadClient.GetStreamAsync(download.DownloadUri, cancellationToken))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            var info = new FileInfo(partialPath);
            if (info.Length != download.Length)
            {
                throw new InvalidDataException("下载的存档长度与云端记录不一致");
            }

            await using (var package = File.OpenRead(partialPath))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(package, cancellationToken));
                if (!string.Equals(hash, download.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("下载的存档 SHA-256 与云端记录不一致");
                }
            }

            File.Move(partialPath, packagePath, true);
            return new DownloadedSnapshotPackage(
                packagePath,
                download.SigningKeyId,
                download.SigningPublicKeyPem);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    public async Task UploadSnapshotAsync(LocalSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        using var createResponse = await httpClient.PostAsJsonAsync(
            $"/api/v1/devices/{credentials.DeviceId}/snapshot-uploads",
            new CreateSnapshotUploadRequest(
                credentials.RoomId,
                snapshot.Name,
                snapshot.Sha256,
                snapshot.Length),
            cancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var upload = await createResponse.Content.ReadFromJsonAsync<SnapshotUploadResponse>(cancellationToken)
            ?? throw new InvalidOperationException("云端存档上传会话无效");

        var partCount = checked((int)((snapshot.Length + upload.PartSize - 1) / upload.PartSize));
        var uploadedParts = new List<UploadedSnapshotPart>(partCount);
        var buffer = GC.AllocateUninitializedArray<byte>(upload.PartSize);
        await using (var package = new FileStream(
            snapshot.PackagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var uploadClient = new HttpClient())
        {
            for (var partNumber = 1; partNumber <= partCount; partNumber++)
            {
                var remaining = snapshot.Length - package.Position;
                var partLength = checked((int)Math.Min(upload.PartSize, remaining));
                await package.ReadExactlyAsync(buffer.AsMemory(0, partLength), cancellationToken);
                var part = await httpClient.GetFromJsonAsync<SnapshotUploadPartResponse>(
                    $"/api/v1/devices/{credentials.DeviceId}/snapshot-uploads/{upload.UploadId}/parts/{partNumber}",
                    cancellationToken) ?? throw new InvalidOperationException("云端没有返回存档分段上传地址");
                using var content = new ReadOnlyMemoryContent(buffer.AsMemory(0, partLength));
                content.Headers.ContentLength = partLength;
                using var uploadResponse = await uploadClient.PutAsync(part.UploadUri, content, cancellationToken);
                uploadResponse.EnsureSuccessStatusCode();
                var etag = uploadResponse.Headers.ETag?.Tag;
                if (string.IsNullOrWhiteSpace(etag))
                {
                    throw new InvalidDataException($"对象存储没有返回第 {partNumber} 段的 ETag");
                }

                uploadedParts.Add(new UploadedSnapshotPart(partNumber, etag));
            }
        }

        using var completeResponse = await httpClient.PostAsJsonAsync(
            $"/api/v1/devices/{credentials.DeviceId}/snapshot-uploads/{upload.UploadId}/complete",
            new CompleteSnapshotUploadRequest(uploadedParts),
            cancellationToken);
        completeResponse.EnsureSuccessStatusCode();
    }

    public AuthenticationHeaderValue CreateAuthorizationHeader() => new(
        "Device",
        $"{credentials.DeviceId}.{credentials.DeviceSecret}");

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private static HttpClient CreateHttpClient(DeviceCredentials deviceCredentials)
    {
        var client = new HttpClient { BaseAddress = deviceCredentials.ServiceUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Device",
            $"{deviceCredentials.DeviceId}.{deviceCredentials.DeviceSecret}");
        return client;
    }
}
