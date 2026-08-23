using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.Obs;

internal static partial class ObsSnapshotMapper
{
    private static readonly string AdapterDefinitionSha256 = Convert.ToHexStringLower(
        SHA256.HashData("obs-websocket-5|video-capture|source-filters|readback"u8));

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

            var capturedSettings = CloneObject(settings, excludeAudioSettings: true);
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
                    ? CloneObject(rawFilterSettings, excludeAudioSettings: false)
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
                ParseDevice(capturedSettings),
                ParseMode(capturedSettings),
                capturedSettings,
                filters));
        }

        sources.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var structure = sources.Select(source => new
        {
            source.Kind,
            Settings = source.Settings.Select(pair => new { pair.Key, Type = pair.Value.ValueKind.ToString() }),
            Filters = source.Filters.Select(filter => new
            {
                filter.Kind,
                Settings = filter.Settings.Select(pair => new { pair.Key, Type = pair.Value.ValueKind.ToString() })
            })
        });
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(structure);
        var coverage = sources.SelectMany(source => source.Settings.Select(pair => new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/settings/{EscapePointerSegment(pair.Key)}",
                "VideoSource",
                pair.Value.ValueKind.ToString(),
                true,
                true,
                "ObsWebSocketReadback")))
            .Concat(sources.SelectMany(source => source.Filters.SelectMany(filter => filter.Settings.Select(pair =>
                new CapturedParameterField(
                    $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/settings/{EscapePointerSegment(pair.Key)}",
                    "VideoFilter",
                    pair.Value.ValueKind.ToString(),
                    true,
                    true,
                    "ObsWebSocketReadback"))))).ToArray();
        coverage = coverage.Concat(sources.SelectMany(source => source.Filters.SelectMany(filter => new[]
        {
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/kind",
                "FilterType",
                "String",
                true,
                true,
                "ObsWebSocketReadback"),
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/enabled",
                "FilterEnabled",
                "Boolean",
                true,
                true,
                "ObsWebSocketReadback"),
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/order",
                "FilterOrder",
                "Number",
                true,
                true,
                "ObsWebSocketReadback")
        }))).ToArray();
        return new ApplicationSnapshot(
            ApplicationKind.Obs,
            version,
            "obs-websocket-5",
            AdapterDefinitionSha256,
            Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
            CompatibilityLevel.Verified,
            true,
            coverage,
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

    private static Dictionary<string, JsonElement> CloneObject(
        JsonElement element,
        bool excludeAudioSettings) => element
        .EnumerateObject()
        .Where(property => !excludeAudioSettings || !IsAudioSetting(property.Name))
        .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);

    private static bool IsAudioSetting(string name) => name.StartsWith("audio_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "use_custom_audio_device", StringComparison.OrdinalIgnoreCase);

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

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
