using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class LanSnapshotConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly Lock configurationLock = new();
    private readonly string settingsPath;
    private string? sharedDirectory;

    public LanSnapshotConfigurationStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio");
        Directory.CreateDirectory(directory);
        settingsPath = Path.Combine(directory, "lan-sync.json");
        sharedDirectory = ReadSharedDirectory();
    }

    public string? SharedDirectory
    {
        get
        {
            lock (configurationLock)
            {
                return sharedDirectory;
            }
        }
    }

    public async Task SaveAsync(
        ConfigureLanDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        var path = NormalizePath(request.Path, mustExist: true);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            new PersistedLanSettings(path),
            JsonOptions);
        var partialPath = $"{settingsPath}.partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(partialPath, content, cancellationToken);
            File.Move(partialPath, settingsPath, true);
            lock (configurationLock)
            {
                sharedDirectory = path;
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

    private string? ReadSharedDirectory()
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<PersistedLanSettings>(
                File.ReadAllBytes(settingsPath),
                JsonOptions);
            return NormalizePath(settings?.SharedDirectory, mustExist: false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (mustExist && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"局域网共享目录不存在: {fullPath}");
        }

        var localSnapshotDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Snapshots"));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(localSnapshotDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("局域网共享目录不能使用 LiveStudio 本机存档目录", nameof(path));
        }

        return fullPath;
    }

    private sealed record PersistedLanSettings(string? SharedDirectory);
}
