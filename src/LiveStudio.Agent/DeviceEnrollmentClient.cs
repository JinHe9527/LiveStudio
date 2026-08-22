using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class DeviceEnrollmentClient(HttpClient httpClient, IDeviceCredentialStore credentialStore)
{
    public async Task EnrollAsync(string token, string machineName, CancellationToken cancellationToken)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new EnrollDeviceRequest(
            token,
            machineName,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            Environment.OSVersion.VersionString,
            signingKey.ExportSubjectPublicKeyInfoPem());
        using var response = await httpClient.PostAsJsonAsync("/api/v1/devices/enroll", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var enrollment = await response.Content.ReadFromJsonAsync<EnrollDeviceResponse>(cancellationToken)
            ?? throw new InvalidOperationException("设备注册响应无效");
        credentialStore.Save(new DeviceCredentials(
            httpClient.BaseAddress ?? throw new InvalidOperationException("云服务地址未设置"),
            enrollment.DeviceId,
            enrollment.OrganizationId,
            enrollment.RoomId,
            enrollment.DeviceSecret,
            signingKey.ExportPkcs8PrivateKeyPem(),
            true));
    }
}
