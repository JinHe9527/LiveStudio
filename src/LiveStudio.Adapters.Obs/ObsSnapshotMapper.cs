using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.Obs;

internal static partial class ObsSnapshotMapper
{
    private static readonly string[] AllowedSourceSettings =
    [
        "video_device_id",
        "video_device_name",
        "res_type",
        "resolution",
        "frame_interval",
        "fps_num",
        "fps_den",
        "video_format",
        "color_space",
        "color_range",
        "buffering"
    ];

    public static async Task<ApplicationSnapshot> CaptureAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken)
    {
        var versionResponse = await client.CallAsync("GetVersion", null, cancellationToken);
        var version = versionResponse.TryGetProperty("obsVersion", out var obsVersion)
            ? obsVersion.GetString() ?? "unknown"
            : "unknown";
        var inputResponse = await client.CallAsync("GetInputList", null, cancellationToken);
        var sources = new List<VideoSource>();
        foreach (var input in inputResponse.GetProperty("inputs").EnumerateArray())
        {
            var inputName = input.GetProperty("inputName").GetString() ?? string.Empty;
            var inputKind = input.GetProperty("inputKind").GetString() ?? string.Empty;
            var settingsResponse = await client.CallAsync(
                "GetInputSettings",
                new { inputName },
                cancellationToken);
            var settings = settingsResponse.GetProperty("inputSettings");
            if (!settings.TryGetProperty("video_device_id", out _)
                && !inputKind.Contains("dshow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sanitizedSettings = SanitizeSourceSettings(settings);
            var filterResponse = await client.CallAsync(
                "GetSourceFilterList",
                new { sourceName = inputName },
                cancellationToken);
            var filters = new List<VideoFilter>();
            foreach (var filter in filterResponse.GetProperty("filters").EnumerateArray())
            {
                var filterKind = filter.GetProperty("filterKind").GetString() ?? string.Empty;
                if (ObsConnectionOptions.BuiltInAudioFilterKinds.Contains(filterKind))
                {
                    continue;
                }

                var filterName = filter.GetProperty("filterName").GetString() ?? string.Empty;
                var filterSettings = filter.TryGetProperty("filterSettings", out var rawFilterSettings)
                    ? CloneObject(rawFilterSettings)
                    : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var assets = await ObsFilterAssetMapper.CaptureAsync(filterSettings, cancellationToken);
                filters.Add(new VideoFilter(
                    CreateLogicalId($"obs|{inputName}|{filterName}"),
                    filterName,
                    filterKind,
                    filter.GetProperty("filterEnabled").GetBoolean(),
                    filter.GetProperty("filterIndex").GetInt32(),
                    filterSettings,
                    assets));
            }

            filters.Sort((left, right) => left.Order.CompareTo(right.Order));
            sources.Add(new VideoSource(
                CreateLogicalId($"obs|{inputName}"),
                inputName,
                inputKind,
                ParseDevice(sanitizedSettings),
                ParseMode(sanitizedSettings),
                sanitizedSettings,
                filters));
        }

        sources.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(sources);
        return new ApplicationSnapshot(
            ApplicationKind.Obs,
            version,
            Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
            sources,
            []);
    }

    public static Dictionary<string, JsonElement> CreateTargetSettings(
        VideoSource source,
        DeviceMapping mapping)
    {
        var settings = source.Settings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        settings["video_device_id"] = JsonSerializer.SerializeToElement(mapping.TargetDeviceId);
        return settings;
    }

    private static Dictionary<string, JsonElement> SanitizeSourceSettings(JsonElement settings)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in AllowedSourceSettings)
        {
            if (settings.TryGetProperty(name, out var value))
            {
                result.Add(name, value.Clone());
            }
        }

        return result;
    }

    private static Dictionary<string, JsonElement> CloneObject(JsonElement element) => element
        .EnumerateObject()
        .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);

    private static CaptureDeviceDescriptor? ParseDevice(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!TryGetString(settings, "video_device_id", out var identifier))
        {
            return null;
        }

        var friendlyName = TryGetString(settings, "video_device_name", out var name) ? name : identifier;
        var match = VidPidPattern().Match(identifier);
        return new CaptureDeviceDescriptor(
            friendlyName,
            match.Success ? match.Groups[1].Value.ToUpperInvariant() : null,
            match.Success ? match.Groups[2].Value.ToUpperInvariant() : null,
            null,
            identifier,
            []);
    }

    private static VideoMode? ParseMode(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!TryGetString(settings, "resolution", out var resolution))
        {
            return null;
        }

        var parts = resolution.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }

        var fpsNumerator = GetInt32(settings, "fps_num") ?? 0;
        var fpsDenominator = Math.Max(1, GetInt32(settings, "fps_den") ?? 1);
        if (fpsNumerator == 0 && GetInt64(settings, "frame_interval") is { } interval && interval > 0)
        {
            fpsNumerator = 10_000_000;
            fpsDenominator = checked((int)interval);
        }

        return new VideoMode(
            width,
            height,
            fpsNumerator,
            fpsDenominator,
            GetRawValue(settings, "video_format"),
            GetRawValue(settings, "color_space"),
            GetRawValue(settings, "color_range"));
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, JsonElement> settings,
        string name,
        out string value)
    {
        if (settings.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static int? GetInt32(IReadOnlyDictionary<string, JsonElement> settings, string name) =>
        settings.TryGetValue(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? GetInt64(IReadOnlyDictionary<string, JsonElement> settings, string name) =>
        settings.TryGetValue(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static string GetRawValue(IReadOnlyDictionary<string, JsonElement> settings, string name) =>
        settings.TryGetValue(name, out var value) ? value.ToString() : string.Empty;

    private static Guid CreateLogicalId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> identifier = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(identifier);
        identifier[6] = (byte)((identifier[6] & 0x0F) | 0x50);
        identifier[8] = (byte)((identifier[8] & 0x3F) | 0x80);
        return new Guid(identifier);
    }

    [GeneratedRegex("vid_([0-9a-f]{4}).*pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidPattern();
}
