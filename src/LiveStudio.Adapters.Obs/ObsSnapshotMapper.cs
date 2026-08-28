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
        IObsAssetPathResolver? assetPathResolver,
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
            var unversionedInputKind = input.TryGetProperty("unversionedInputKind", out var rawUnversionedKind)
                ? rawUnversionedKind.GetString() ?? inputKind
                : inputKind;
            var settingsResponse = await client.CallAsync(
                "GetInputSettings",
                new { inputName },
                cancellationToken);
            var settings = settingsResponse.GetProperty("inputSettings");
            var isConfirmedVideoCapture = IsConfirmedVideoCapture(inputKind, settings);
            if (!isConfirmedVideoCapture && IsKnownAudioInput(inputKind))
            {
                continue;
            }

            var capturedSettings = CloneObject(settings, excludeAudioSettings: true);
            var defaultSettingsResponse = await TryGetOptionalDefaultsAsync(
                client,
                "GetInputDefaultSettings",
                new { inputKind },
                cancellationToken);
            var defaultSettings = defaultSettingsResponse is { } inputDefaults
                                  && inputDefaults.TryGetProperty("defaultInputSettings", out var rawDefaults)
                ? CloneObject(rawDefaults, excludeAudioSettings: true)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
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
                var explicitFilterSettings = filter.TryGetProperty("filterSettings", out var rawFilterSettings)
                    ? CloneObject(rawFilterSettings, excludeAudioSettings: false)
                    : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var defaultFilterSettingsResponse = await TryGetOptionalDefaultsAsync(
                    client,
                    "GetSourceFilterDefaultSettings",
                    new { filterKind },
                    cancellationToken);
                var defaultFilterSettings = defaultFilterSettingsResponse is { } filterDefaults
                                            && filterDefaults.TryGetProperty(
                    "defaultFilterSettings",
                    out var rawFilterDefaults)
                    ? CloneObject(rawFilterDefaults, excludeAudioSettings: false)
                    : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var filterSettings = ObsFilterAssetMapper.ResolveMissingAssets(
                    MergeEffectiveFilterSettings(defaultFilterSettings, explicitFilterSettings),
                    assetPathResolver,
                    rejectUnresolved: true);
                var assets = await ObsFilterAssetMapper.CaptureAsync(filterSettings, cancellationToken);
                filters.Add(new VideoFilter(
                    CreateLogicalId($"obs|{inputName}|{filterName}"),
                    filterName,
                    filterKind,
                    filter.GetProperty("filterEnabled").GetBoolean(),
                    filters.Count,
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
                filters,
                unversionedInputKind,
                defaultSettings));
        }

        sources.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var structure = sources.Select(source => new
        {
            source.Kind,
            source.UnversionedKind,
            Settings = source.Settings.Select(pair => new { pair.Key, Shape = DescribeShape(pair.Value) }),
            Filters = source.Filters.Select(filter => new
            {
                filter.Kind,
                Settings = filter.Settings.Select(pair => new { pair.Key, Shape = DescribeShape(pair.Value) })
            })
        });
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(structure);
        var coverage = sources.SelectMany(source => source.Settings.SelectMany(pair => EnumerateCoverage(
                pair.Value,
                $"/sources/{source.LogicalId:N}/settings/{EscapePointerSegment(pair.Key)}",
                "VideoSource",
                $"OBS/{source.Name}/inputSettings/{pair.Key}",
                pair.Key,
                EvidenceStatus(source))))
            .Concat(sources.SelectMany(source => source.Filters.SelectMany(filter => filter.Settings.SelectMany(pair =>
                EnumerateCoverage(
                    pair.Value,
                    $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/settings/{EscapePointerSegment(pair.Key)}",
                    "VideoFilter",
                    $"OBS/{source.Name}/视频滤镜/{filter.Name}/{pair.Key}",
                    pair.Key,
                    EvidenceStatus(source))))))
            .Concat(sources.SelectMany(source => new[]
            {
                new CapturedParameterField(
                    $"/sources/{source.LogicalId:N}/kind",
                    "InputKind",
                    "String",
                    true,
                    IsConfirmedVideoCapture(source.Kind, source.Settings),
                    "ObsWebSocketReadback",
                    $"obs:{source.LogicalId:N}:kind",
                    "inputKind",
                    $"OBS/{source.Name}/inputKind",
                    EvidenceStatus(source)),
                new CapturedParameterField(
                    $"/sources/{source.LogicalId:N}/unversionedKind",
                    "InputKind",
                    "String",
                    true,
                    IsConfirmedVideoCapture(source.Kind, source.Settings),
                    "ObsWebSocketReadback",
                    $"obs:{source.LogicalId:N}:unversioned-kind",
                    "unversionedInputKind",
                    $"OBS/{source.Name}/unversionedInputKind",
                    EvidenceStatus(source))
            }))
            .ToArray();
        coverage = coverage.Concat(sources.SelectMany(source => source.Filters.SelectMany(filter => new[]
        {
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/kind",
                "FilterType",
                "String",
                IsConfirmedVideoCapture(source.Kind, source.Settings),
                true,
                "ObsWebSocketReadback",
                $"obs:{source.LogicalId:N}:{filter.LogicalId:N}:kind",
                "kind",
                $"OBS/{source.Name}/视频滤镜/{filter.Name}/kind",
                EvidenceStatus(source)),
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/enabled",
                "FilterEnabled",
                "Boolean",
                IsConfirmedVideoCapture(source.Kind, source.Settings),
                true,
                "ObsWebSocketReadback",
                $"obs:{source.LogicalId:N}:{filter.LogicalId:N}:enabled",
                "enabled",
                $"OBS/{source.Name}/视频滤镜/{filter.Name}/enabled",
                EvidenceStatus(source)),
            new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}/order",
                "FilterOrder",
                "Number",
                IsConfirmedVideoCapture(source.Kind, source.Settings),
                true,
                "ObsWebSocketReadback",
                $"obs:{source.LogicalId:N}:{filter.LogicalId:N}:order",
                "order",
                $"OBS/{source.Name}/视频滤镜/{filter.Name}/order",
                EvidenceStatus(source))
        }))).ToArray();
        var configurationTree = CreateConfigurationTree(sources, coverage);
        var filterChains = sources.Select(source => new FilterChainSnapshot(
            source.LogicalId,
            "视频滤镜",
            $"OBS/{source.Name}/视频滤镜",
            null,
            source.Filters.Select(filter => new FilterInstanceSnapshot(
                filter.LogicalId,
                filter.Name,
                filter.Kind,
                null,
                filter.Enabled,
                filter.Order,
                filter.Settings,
                filter.Assets,
                EvidenceStatus(source))).ToArray())).ToArray();
        var hasCompleteInventory = configurationTree.HasCompleteUiInventory
                                   && configurationTree.HasCompleteNativeInventory;
        return new ApplicationSnapshot(
            ApplicationKind.Obs,
            version,
            "obs-websocket-5",
            AdapterDefinitionSha256,
            Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
            hasCompleteInventory ? CompatibilityLevel.Verified : CompatibilityLevel.Experimental,
            true,
            coverage,
            sources,
            [],
            configurationTree,
            filterChains);
    }

    public static Dictionary<string, JsonElement> CreateTargetSettings(
        VideoSource source,
        DeviceMapping mapping)
    {
        var settings = source.Settings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        if (settings.ContainsKey("video_device_id"))
        {
            settings["video_device_id"] = JsonSerializer.SerializeToElement(mapping.TargetDeviceId);
        }

        return settings;
    }

    internal static Dictionary<string, JsonElement> MergeEffectiveFilterSettings(
        IReadOnlyDictionary<string, JsonElement> defaultSettings,
        IReadOnlyDictionary<string, JsonElement> explicitSettings)
    {
        var effective = defaultSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        foreach (var setting in explicitSettings)
        {
            effective[setting.Key] = setting.Value.Clone();
        }

        return effective;
    }

    private static async Task<JsonElement?> TryGetOptionalDefaultsAsync(
        ObsWebSocketClient client,
        string requestType,
        object requestData,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.CallAsync(requestType, requestData, cancellationToken);
        }
        catch (ObsRequestException exception) when (IsOptionalDefaultSettingsFailure(exception, requestType))
        {
            // Some input/filter plug-ins do not implement optional defaults requests. Explicit
            // settings remain authoritative; transport and authentication failures are not hidden.
            return null;
        }
    }

    internal static bool IsOptionalDefaultSettingsFailure(
        ObsRequestException exception,
        string requestType) =>
        exception.StatusCode is not null
        && string.Equals(exception.RequestType, requestType, StringComparison.Ordinal)
        && requestType is "GetInputDefaultSettings" or "GetSourceFilterDefaultSettings";

    public static Dictionary<string, JsonElement> PreserveExcludedAudioSettings(
        IReadOnlyDictionary<string, JsonElement> targetVideoSettings,
        JsonElement currentSettings)
    {
        var merged = targetVideoSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        foreach (var property in currentSettings.EnumerateObject().Where(property => IsAudioSetting(property.Name)))
        {
            merged[property.Name] = property.Value.Clone();
        }

        return merged;
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

    private static IEnumerable<CapturedParameterField> EnumerateCoverage(
        JsonElement value,
        string nativePath,
        string category,
        string uiPath,
        string nativeName,
        FieldEvidenceStatus evidenceStatus)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                foreach (var field in EnumerateCoverage(
                             property.Value,
                             $"{nativePath}/{EscapePointerSegment(property.Name)}",
                             category,
                             $"{uiPath}/{property.Name}",
                             property.Name,
                             evidenceStatus))
                {
                    yield return field;
                }
            }

            if (value.EnumerateObject().Any())
            {
                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var field in EnumerateCoverage(
                             item,
                             $"{nativePath}/{index}",
                             category,
                             $"{uiPath}/{index}",
                             index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                             evidenceStatus))
                {
                    yield return field;
                }

                index++;
            }

            if (index > 0)
            {
                yield break;
            }
        }

        yield return new CapturedParameterField(
            nativePath,
            category,
            value.ValueKind.ToString(),
            evidenceStatus >= FieldEvidenceStatus.Mapped,
            true,
            "ObsWebSocketReadback",
            $"obs:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nativePath)))[..24]}",
            nativeName,
            uiPath,
            evidenceStatus);
    }

    private static ConfigurationTreeSnapshot CreateConfigurationTree(
        IReadOnlyList<VideoSource> sources,
        CapturedParameterField[] coverage)
    {
        var sections = sources.Select((source, sourceIndex) =>
        {
            var sourcePrefix = $"/sources/{source.LogicalId:N}/";
            var sourceFields = coverage.Where(field => field.NativePath.StartsWith(sourcePrefix, StringComparison.Ordinal)).ToArray();
            var inputFields = sourceFields.Where(field => field.NativePath.Contains("/settings/", StringComparison.Ordinal))
                .Select((field, index) => CreateConfigurationField(field, index, source.Settings, source.DefaultSettings, null))
                .ToArray();
            var filterFields = sourceFields.Where(field => field.NativePath.Contains("/filters/", StringComparison.Ordinal))
                .Select((field, index) => CreateConfigurationField(field, index, null, null, source.Filters))
                .ToArray();
            return new ConfigurationSectionSnapshot(
                $"obs-source-{source.LogicalId:N}",
                source.Name,
                $"OBS/{source.Name}",
                sourceIndex,
                [
                    new ConfigurationSectionSnapshot(
                        $"obs-source-{source.LogicalId:N}-input-settings",
                        "inputSettings",
                        $"OBS/{source.Name}/inputSettings",
                        0,
                        [],
                        inputFields),
                    new ConfigurationSectionSnapshot(
                        $"obs-source-{source.LogicalId:N}-filters",
                        "视频滤镜",
                        $"OBS/{source.Name}/视频滤镜",
                        1,
                        [],
                        filterFields)
                ],
                []);
        }).ToArray();
        return new ConfigurationTreeSnapshot(
            sections,
            coverage.Count(field => field.EvidenceStatus == FieldEvidenceStatus.Unknown),
            0,
            coverage.Count(field => field.EvidenceStatus == FieldEvidenceStatus.Mapped),
            0,
            coverage.Length > 0 && coverage.All(field => field.EvidenceStatus >= FieldEvidenceStatus.Mapped),
            coverage.Length > 0 && coverage.All(field => field.EvidenceStatus >= FieldEvidenceStatus.Mapped));
    }

    private static FieldEvidenceStatus EvidenceStatus(VideoSource source) => FieldEvidenceStatus.Mapped;

    private static bool IsConfirmedVideoCapture(
        string inputKind,
        IReadOnlyDictionary<string, JsonElement> settings) =>
        settings.ContainsKey("video_device_id")
        || inputKind.Contains("dshow", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfirmedVideoCapture(string inputKind, JsonElement settings) =>
        settings.TryGetProperty("video_device_id", out _)
        || inputKind.Contains("dshow", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownAudioInput(string inputKind) =>
        !inputKind.Contains("dshow", StringComparison.OrdinalIgnoreCase)
        && (inputKind.Contains("wasapi", StringComparison.OrdinalIgnoreCase)
            || inputKind.Contains("audio", StringComparison.OrdinalIgnoreCase));

    private static ConfigurationFieldSnapshot CreateConfigurationField(
        CapturedParameterField field,
        int order,
        IReadOnlyDictionary<string, JsonElement>? sourceSettings,
        IReadOnlyDictionary<string, JsonElement>? defaultSettings,
        IReadOnlyList<VideoFilter>? filters)
    {
        var value = TryResolveObsValue(field.NativePath, sourceSettings, filters)
            ?? JsonSerializer.SerializeToElement<object?>(null);
        var defaultValue = defaultSettings is null
            ? (JsonElement?)null
            : TryResolveObsValue(field.NativePath, defaultSettings, null);
        return new ConfigurationFieldSnapshot(
            field.FieldId,
            field.NativeName,
            field.UiPath,
            order,
            field.ValueType,
            "NativeValue",
            value,
            defaultValue,
            null,
            null,
            null,
            [],
            null,
            new NativeLocatorSnapshot("ObsWebSocket", "obs", field.NativePath, null, field.ValueType),
            field.EvidenceStatus,
            field.Writable,
            []);
    }

    private static JsonElement? TryResolveObsValue(
        string path,
        IReadOnlyDictionary<string, JsonElement>? sourceSettings,
        IReadOnlyList<VideoFilter>? filters)
    {
        var settingsMarker = "/settings/";
        var markerIndex = path.IndexOf(settingsMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            if (filters is null)
            {
                return null;
            }

            var filter = filters.FirstOrDefault(candidate => path.Contains(candidate.LogicalId.ToString("N"), StringComparison.Ordinal));
            if (filter is null)
            {
                return null;
            }

            if (path.EndsWith("/kind", StringComparison.Ordinal)) return JsonSerializer.SerializeToElement(filter.Kind);
            if (path.EndsWith("/enabled", StringComparison.Ordinal)) return JsonSerializer.SerializeToElement(filter.Enabled);
            if (path.EndsWith("/order", StringComparison.Ordinal)) return JsonSerializer.SerializeToElement(filter.Order);
            return null;
        }

        var pointer = path[(markerIndex + settingsMarker.Length)..];
        var root = sourceSettings;
        if (filters is not null)
        {
            var filter = filters.FirstOrDefault(candidate => path.Contains(candidate.LogicalId.ToString("N"), StringComparison.Ordinal));
            root = filter?.Settings;
        }

        if (root is null)
        {
            return null;
        }

        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(UnescapePointerSegment).ToArray();
        if (segments.Length == 0 || !root.TryGetValue(segments[0], out var current))
        {
            return null;
        }

        for (var index = 1; index < segments.Length; index++)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segments[index], out var property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array
                     && int.TryParse(segments[index], out var itemIndex)
                     && itemIndex >= 0
                     && itemIndex < current.GetArrayLength())
            {
                current = current[itemIndex];
            }
            else
            {
                return null;
            }
        }

        return current.Clone();
    }

    private static string UnescapePointerSegment(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private static object DescribeShape(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(property => new
        {
            property.Name,
            Shape = DescribeShape(property.Value)
        }).ToArray(),
        JsonValueKind.Array => value.EnumerateArray().Select(DescribeShape).ToArray(),
        _ => value.ValueKind.ToString()
    };

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
