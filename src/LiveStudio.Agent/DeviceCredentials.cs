using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Agent;

public sealed record DeviceCredentials(
    Uri ServiceUri,
    Guid DeviceId,
    Guid OrganizationId,
    Guid RoomId,
    string DeviceSecret,
    string PackageSigningPrivateKeyPem,
    bool IsCloudEnrolled);

public interface IDeviceCredentialStore
{
    void Save(DeviceCredentials credentials);

    DeviceCredentials Load();

    bool TryLoad([NotNullWhen(true)] out DeviceCredentials? credentials);
}

public sealed class WindowsCredentialStore : IDeviceCredentialStore
{
    private const string CredentialTarget = "LiveStudio/Agent";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Save(DeviceCredentials credentials)
    {
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials, JsonOptions));
        if (content.Length > 5_120)
        {
            throw new InvalidOperationException("设备凭据超过 Credential Manager 大小限制");
        }

        try
        {
            WindowsCredentialManager.Write(
                CredentialTarget,
                credentials.DeviceId.ToString("N"),
                content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public DeviceCredentials Load()
    {
        if (TryLoad(out var credentials))
        {
            return credentials;
        }

        throw new InvalidOperationException("未找到 Agent 设备凭据，请先完成设备注册");
    }

    public bool TryLoad([NotNullWhen(true)] out DeviceCredentials? credentials)
    {
        if (!WindowsCredentialManager.TryRead(CredentialTarget, out var content))
        {
            credentials = null;
            return false;
        }

        try
        {
            credentials = JsonSerializer.Deserialize<DeviceCredentials>(content, JsonOptions)
                ?? throw new InvalidOperationException("无法解析 Agent 设备凭据");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public void EnsureLocalIdentity()
    {
        if (TryLoad(out _))
        {
            return;
        }

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Save(new DeviceCredentials(
            new Uri("https://local.livestudio.invalid"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            string.Empty,
            signingKey.ExportPkcs8PrivateKeyPem(),
            false));
    }
}
