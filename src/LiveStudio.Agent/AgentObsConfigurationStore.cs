using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class AgentObsConfigurationStore : IObsConnectionOptionsProvider, IObsCredentialProvider
{
    private const string PasswordCredentialTarget = "LiveStudio/OBSWebSocket";
    private static readonly Uri DefaultEndpoint = new("ws://127.0.0.1:4455");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly Lock configurationLock = new();
    private readonly string settingsPath;
    private ObsConnectionOptions current;

    public AgentObsConfigurationStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio");
        Directory.CreateDirectory(directory);
        settingsPath = Path.Combine(directory, "agent-settings.json");
        current = new ObsConnectionOptions(ReadEndpoint(), ObsConnectionOptions.BuiltInVideoFilterKinds);
    }

    public ObsConnectionOptions Current
    {
        get
        {
            lock (configurationLock)
            {
                return current;
            }
        }
    }

    public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsCredentialManager.TryRead(PasswordCredentialTarget, out var content))
        {
            return ValueTask.FromResult(string.Empty);
        }

        try
        {
            return ValueTask.FromResult(Encoding.UTF8.GetString(content));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    public async Task SaveAsync(ConfigureObsRequest request, CancellationToken cancellationToken)
    {
        ValidateEndpoint(request.Endpoint);
        var password = Encoding.UTF8.GetBytes(request.Password);
        try
        {
            WindowsCredentialManager.Write(PasswordCredentialTarget, "OBS WebSocket", password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }

        var settings = new PersistedAgentSettings(request.Endpoint.ToString());
        var content = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var partialPath = $"{settingsPath}.partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(partialPath, content, cancellationToken);
            File.Move(partialPath, settingsPath, true);
            lock (configurationLock)
            {
                current = new ObsConnectionOptions(
                    request.Endpoint,
                    ObsConnectionOptions.BuiltInVideoFilterKinds);
            }
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private Uri ReadEndpoint()
    {
        if (!File.Exists(settingsPath))
        {
            return DefaultEndpoint;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<PersistedAgentSettings>(
                File.ReadAllBytes(settingsPath),
                JsonOptions);
            if (settings is null || !Uri.TryCreate(settings.ObsEndpoint, UriKind.Absolute, out var endpoint))
            {
                return DefaultEndpoint;
            }

            ValidateEndpoint(endpoint);
            return endpoint;
        }
        catch (JsonException)
        {
            return DefaultEndpoint;
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme is not "ws" and not "wss" || !endpoint.IsLoopback)
        {
            throw new ArgumentException("OBS WebSocket 地址必须是本机 ws:// 或 wss:// 地址", nameof(endpoint));
        }
    }

    private sealed record PersistedAgentSettings(string ObsEndpoint);
}
