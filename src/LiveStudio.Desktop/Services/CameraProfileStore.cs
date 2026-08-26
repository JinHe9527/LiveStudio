using System.Text.Json;

namespace LiveStudio.Desktop.Services;

public enum CameraProfileMode
{
    Manual,
    Wired
}

public sealed record CameraCreativeLookSettings(
    int Contrast,
    int Highlights,
    int Shadows,
    int Fade,
    int Saturation,
    int Sharpness,
    int SharpnessRange,
    int Clarity)
{
    public static CameraCreativeLookSettings StandardDefault { get; } = new(
        Contrast: 0,
        Highlights: 0,
        Shadows: 0,
        Fade: 0,
        Saturation: 0,
        Sharpness: 0,
        SharpnessRange: 0,
        Clarity: 0);

    public bool IsValid =>
        Contrast is >= -9 and <= 9
        && Highlights is >= -9 and <= 9
        && Shadows is >= -9 and <= 9
        && Fade is >= 0 and <= 9
        && Saturation is >= -9 and <= 9
        && Sharpness is >= 0 and <= 9
        && SharpnessRange is >= 0 and <= 5
        && Clarity is >= 0 and <= 9;
}

public sealed record CameraProfile(
    Guid Id,
    string Name,
    DateTimeOffset UpdatedAt,
    CameraProfileMode Mode,
    string Aperture,
    string ShutterSpeed,
    string Iso,
    string CreativeLook,
    string? CameraName = null,
    CameraCreativeLookSettings? CreativeLookSettings = null,
    int? StationSlot = null)
{
    public CameraCreativeLookSettings EffectiveCreativeLookSettings =>
        CreativeLookSettings ?? CameraCreativeLookSettings.StandardDefault;
}

public sealed class CameraProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string filePath;

    public CameraProfileStore(string? filePath = null)
    {
        this.filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "camera-profiles.json");
    }

    public async Task<IReadOnlyList<CameraProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var profiles = await JsonSerializer.DeserializeAsync<CameraProfile[]>(
                stream,
                JsonOptions,
                cancellationToken);
            return (profiles ?? [])
                .Where(IsValidStoredProfile)
                .OrderByDescending(profile => profile.UpdatedAt)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("相机档案文件格式无效", exception);
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<CameraProfile> profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Any(profile => !IsValidStoredProfile(profile)))
        {
            throw new InvalidDataException("相机档案包含空名称或空参数");
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("无法确定相机档案目录");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{filePath}.partial-{Guid.NewGuid():N}";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profiles.OrderByDescending(profile => profile.UpdatedAt).ToArray(),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValidStoredProfile(CameraProfile profile) =>
        profile.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(profile.Name)
        && !string.IsNullOrWhiteSpace(profile.Aperture)
        && !string.IsNullOrWhiteSpace(profile.ShutterSpeed)
        && !string.IsNullOrWhiteSpace(profile.Iso)
        && !string.IsNullOrWhiteSpace(profile.CreativeLook)
        && (profile.CreativeLookSettings?.IsValid ?? true)
        && profile.StationSlot is null or >= 0 and <= 2;
}
