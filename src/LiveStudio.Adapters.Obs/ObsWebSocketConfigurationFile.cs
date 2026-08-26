using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveStudio.Adapters.Obs;

public sealed record ObsWebSocketConfiguration(
    bool ServerEnabled,
    bool AuthenticationRequired,
    int Port,
    string Password);

public sealed class ObsWebSocketConfigurationFile(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    public ObsWebSocketConfiguration Read() => ReadConfiguration(ReadRoot());

    public ObsWebSocketConfigurationTransaction EnableAuthenticated()
    {
        var original = File.ReadAllBytes(Path);
        var root = JsonNode.Parse(original)?.AsObject()
            ?? throw new InvalidDataException("OBS WebSocket 配置不是有效的 JSON 对象");
        var current = ReadConfiguration(root);
        var password = string.IsNullOrWhiteSpace(current.Password)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : current.Password;
        var port = current.Port is > 0 and <= 65535 ? current.Port : 4455;

        root["server_enabled"] = true;
        root["auth_required"] = true;
        root["server_port"] = port;
        root["server_password"] = password;

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("无法确定 OBS WebSocket 配置目录");
        var transactionId = Guid.NewGuid().ToString("N");
        var partialPath = System.IO.Path.Combine(directory, $"config.json.partial-{transactionId}");
        var backupPath = System.IO.Path.Combine(directory, $"config.json.livestudio-backup-{transactionId}");
        try
        {
            File.WriteAllText(partialPath, root.ToJsonString(JsonOptions));
            File.Move(Path, backupPath);
            try
            {
                File.Move(partialPath, Path);
            }
            catch
            {
                File.Move(backupPath, Path);
                throw;
            }
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }

        return new ObsWebSocketConfigurationTransaction(
            Path,
            backupPath,
            new ObsWebSocketConfiguration(true, true, port, password));
    }

    private JsonObject ReadRoot()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException("没有找到 OBS WebSocket 配置，请先启动一次 OBS", Path);
        }

        return JsonNode.Parse(File.ReadAllBytes(Path))?.AsObject()
            ?? throw new InvalidDataException("OBS WebSocket 配置不是有效的 JSON 对象");
    }

    private static ObsWebSocketConfiguration ReadConfiguration(JsonObject root)
    {
        var enabled = root["server_enabled"]?.GetValue<bool>() ?? false;
        var authenticationRequired = root["auth_required"]?.GetValue<bool>() ?? false;
        var port = root["server_port"]?.GetValue<int>() ?? 4455;
        var password = root["server_password"]?.GetValue<string>() ?? string.Empty;
        return new ObsWebSocketConfiguration(enabled, authenticationRequired, port, password);
    }
}

public sealed class ObsWebSocketConfigurationTransaction : IDisposable
{
    private readonly string configurationPath;
    private readonly string backupPath;
    private bool completed;

    internal ObsWebSocketConfigurationTransaction(
        string configurationPath,
        string backupPath,
        ObsWebSocketConfiguration configuration)
    {
        this.configurationPath = configurationPath;
        this.backupPath = backupPath;
        Configuration = configuration;
    }

    public ObsWebSocketConfiguration Configuration { get; }

    public void Commit()
    {
        completed = true;
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    public void Dispose()
    {
        if (completed || !File.Exists(backupPath))
        {
            return;
        }

        File.Move(backupPath, configurationPath, true);
        completed = true;
    }
}
