using System.Globalization;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed class SnapshotInspectorViewModel
{
    public SnapshotInspectorViewModel(
        string name,
        DateTimeOffset createdAt,
        IReadOnlyList<ApplicationSnapshot> applications,
        string verification = "",
        string verificationDetail = "")
    {
        Name = name;
        CreatedAt = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        Applications = applications.Select(application => new SnapshotApplicationViewModel(application)).ToArray();
        var sourceCount = applications.Sum(application => application.Sources.Count);
        var filterCount = applications.Sum(application => application.Sources.Sum(source => source.Filters.Count));
        Summary = $"{applications.Count} 个应用 · {sourceCount} 个视频来源 · {filterCount} 个滤镜";
        Verification = verification;
        VerificationDetail = verificationDetail;
        DeclaredFieldCount = Applications.Sum(application => application.DeclaredFieldCount);
        CapturedFieldCount = Applications.Sum(application => application.CapturedFieldCount);
        GapCount = Applications.Sum(application => application.GapCount);
        CoverageStatus = GapCount == 0 && DeclaredFieldCount > 0
            ? "未发现参数遗漏"
            : GapCount == 0
                ? "没有字段覆盖声明"
                : $"发现 {GapCount} 项参数缺口";
        CoverageDetail = $"逐项核对 {DeclaredFieldCount} 项 · 可验证 {CapturedFieldCount} 项 · 缺口 {GapCount} 项";
    }

    public string Name { get; }

    public string CreatedAt { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotApplicationViewModel> Applications { get; }

    public string Verification { get; }

    public string VerificationDetail { get; }

    public bool HasVerification => !string.IsNullOrWhiteSpace(Verification);

    public int DeclaredFieldCount { get; }

    public int CapturedFieldCount { get; }

    public int GapCount { get; }

    public bool HasCoverageGaps => GapCount > 0 || DeclaredFieldCount == 0;

    public string CoverageStatus { get; }

    public string CoverageDetail { get; }
}

public sealed class SnapshotApplicationViewModel
{
    public SnapshotApplicationViewModel(ApplicationSnapshot application)
    {
        Name = application.Kind == ApplicationKind.Obs ? "OBS Studio" : "抖音直播伴侣";
        Version = $"版本 {application.Version}";
        AdapterStatus = application.Compatibility switch
        {
            CompatibilityLevel.Verified => $"已验证适配器 · {application.AdapterId}",
            CompatibilityLevel.Experimental => $"实验适配器 · {application.AdapterId}",
            _ => $"不可恢复 · {application.AdapterId}"
        };
        AdapterId = application.AdapterId;
        AdapterDefinition = ShortHash(application.AdapterDefinitionSha256);
        StructureFingerprint = ShortHash(application.StructureFingerprint);
        Sources = application.Sources.Select(source => new SnapshotSourceViewModel(source)).ToArray();
        CoverageFields = CreateCoverageFields(application);
        DeclaredFieldCount = CoverageFields.Count;
        CapturedFieldCount = CoverageFields.Count(field => !field.IsGap);
        GapCount = CoverageFields.Count(field => field.IsGap);
        CoverageStatus = GapCount == 0 && application.FieldCoverage.Count > 0
            ? "字段覆盖完整"
            : application.FieldCoverage.Count == 0
                ? "没有覆盖声明"
                : $"{GapCount} 项缺失或未声明";
        CoverageCount = $"逐项核对 {CapturedFieldCount} / {DeclaredFieldCount}";
        Summary = DeclaredFieldCount == 0
            ? $"{Sources.Count} 个视频来源 · {Sources.Sum(source => source.Filters.Count)} 个滤镜"
            : $"{Sources.Count} 个视频来源 · {Sources.Sum(source => source.Filters.Count)} 个滤镜 · 可验证 {CapturedFieldCount}/{DeclaredFieldCount} 项字段";
    }

    public string Name { get; }

    public string Version { get; }

    public string AdapterStatus { get; }

    public string AdapterId { get; }

    public string AdapterDefinition { get; }

    public string StructureFingerprint { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotSourceViewModel> Sources { get; }

    public IReadOnlyList<SnapshotCoverageFieldViewModel> CoverageFields { get; }

    public int DeclaredFieldCount { get; }

    public int CapturedFieldCount { get; }

    public int GapCount { get; }

    public bool HasCoverageGaps => GapCount > 0 || DeclaredFieldCount == 0;

    public string CoverageStatus { get; }

    public string CoverageCount { get; }

    private static SnapshotCoverageFieldViewModel[] CreateCoverageFields(
        ApplicationSnapshot application)
    {
        var actualValues = FlattenValues(application);
        var cannotVerifyRestore = application.Compatibility == CompatibilityLevel.Unsupported;
        var declaredPaths = application.FieldCoverage
            .Select(field => field.NativePath)
            .ToHashSet(StringComparer.Ordinal);
        var fields = application.FieldCoverage.SelectMany(field =>
            actualValues.TryGetValue(field.NativePath, out var value)
                ? SnapshotCoverageFieldViewModel.CreateCaptured(field, value, cannotVerifyRestore)
                : [new SnapshotCoverageFieldViewModel(field, null, true)]).ToList();
        fields.AddRange(actualValues
            .Where(pair => !declaredPaths.Contains(pair.Key))
            .SelectMany(pair => SnapshotCoverageFieldViewModel.CreateUndeclared(pair.Key, pair.Value)));
        return fields
            .OrderByDescending(field => field.IsGap)
            .ThenBy(field => field.TechnicalPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, JsonElement> FlattenValues(ApplicationSnapshot application)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (application.Kind == ApplicationKind.Obs)
        {
            foreach (var source in application.Sources)
            {
                foreach (var setting in source.Settings)
                {
                    values[$"/sources/{source.LogicalId:N}/settings/{EscapePointerSegment(setting.Key)}"] = setting.Value.Clone();
                }

                foreach (var filter in source.Filters)
                {
                    var prefix = $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}";
                    values[$"{prefix}/kind"] = JsonSerializer.SerializeToElement(filter.Kind);
                    values[$"{prefix}/enabled"] = JsonSerializer.SerializeToElement(filter.Enabled);
                    values[$"{prefix}/order"] = JsonSerializer.SerializeToElement(filter.Order);
                    foreach (var setting in filter.Settings)
                    {
                        values[$"{prefix}/settings/{EscapePointerSegment(setting.Key)}"] = setting.Value.Clone();
                    }
                }
            }
        }

        foreach (var document in application.NativeDocuments)
        {
            foreach (var value in document.Values)
            {
                values[$"{document.RelativePath}:{value.JsonPointer}"] = value.Value.Clone();
            }
        }

        return values;
    }

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string ShortHash(string value) => string.IsNullOrWhiteSpace(value)
        ? "未记录"
        : value.Length <= 20 ? value : $"{value[..12]}…{value[^8..]}";
}

public sealed class SnapshotCoverageFieldViewModel
{
    public SnapshotCoverageFieldViewModel(
        CapturedParameterField field,
        string? value,
        bool isGap)
    {
        Name = FieldName(field.Category, field.NativePath);
        TechnicalPath = field.NativePath;
        Value = value ?? "没有捕获到值";
        Type = field.ValueType;
        Requirement = field.Required ? "必需" : "可选";
        Access = field.Writable ? "可恢复" : "只读";
        Verification = VerificationName(field.Verification);
        Status = isGap
            ? field.Verification == "DiscoveryReadOnly" ? "待适配" : "缺失"
            : "已覆盖";
        IsGap = isGap;
    }

    public string Name { get; }

    public string TechnicalPath { get; }

    public string Value { get; }

    public string Type { get; }

    public string Requirement { get; }

    public string Access { get; }

    public string Verification { get; }

    public string Status { get; }

    public bool IsGap { get; }

    public static SnapshotCoverageFieldViewModel[] CreateCaptured(
        CapturedParameterField field,
        JsonElement value,
        bool cannotVerifyRestore) => Expand(field, field.NativePath, value, cannotVerifyRestore).ToArray();

    public static SnapshotCoverageFieldViewModel[] CreateUndeclared(
        string path,
        JsonElement value)
    {
        var field = new CapturedParameterField(
            path,
            "Undeclared",
            value.ValueKind.ToString(),
            false,
            false,
            "NoCoverageDeclaration");
        return Expand(field, path, value, true).ToArray();
    }

    private static IEnumerable<SnapshotCoverageFieldViewModel> Expand(
        CapturedParameterField field,
        string path,
        JsonElement value,
        bool isGap)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    foreach (var child in Expand(
                                 field with { NativePath = $"{path}/{EscapePointerSegment(property.Name)}" },
                                 $"{path}/{EscapePointerSegment(property.Name)}",
                                 property.Value,
                                 isGap))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (items.Length > 0)
            {
                for (var index = 0; index < items.Length; index++)
                {
                    foreach (var child in Expand(
                                 field with { NativePath = $"{path}/{index}" },
                                 $"{path}/{index}",
                                 items[index],
                                 isGap))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }

        yield return new SnapshotCoverageFieldViewModel(
            field with { NativePath = path, ValueType = value.ValueKind.ToString() },
            FormatRawValue(value),
            isGap);
    }

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static string FormatRawValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };

    private static string FieldName(string category, string path)
    {
        var segments = path.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var leaf = UnescapePointerSegment(segments.LastOrDefault() ?? path);
        var name = category switch
        {
            "DeviceSelection" or "VideoSource" => SnapshotParameterPresentation.PresentTechnicalName(leaf),
            "Width" => "分辨率宽度",
            "Height" => "分辨率高度",
            "FramesPerSecond" => "帧率",
            "PixelFormat" => "像素格式",
            "ColorSpace" => "色彩空间",
            "ColorRange" => "色彩范围",
            "FilterType" => "滤镜类型",
            "FilterEnabled" => "滤镜启用状态",
            "FilterOrder" => "滤镜顺序",
            "FilterSetting" or "VideoFilter" or "Filter" => SnapshotParameterPresentation.PresentTechnicalName(leaf),
            "FilterAsset" => "滤镜素材",
            "Undeclared" => $"未声明字段 · {leaf}",
            _ => SnapshotParameterPresentation.PresentTechnicalName(leaf)
        };
        if (segments.Length >= 2
            && int.TryParse(segments[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var pointIndex))
        {
            return $"{name} · 第 {pointIndex + 1} 个控制点";
        }

        return int.TryParse(leaf, NumberStyles.None, CultureInfo.InvariantCulture, out var itemIndex)
            ? $"第 {itemIndex + 1} 项"
            : name;
    }

    private static string UnescapePointerSegment(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);

    private static string VerificationName(string verification) => verification switch
    {
        "ObsWebSocketReadback" => "OBS WebSocket 回读",
        "SignedAdapterReadback" => "签名适配器回读",
        "NativeFieldReadback" => "原生字段回读",
        "DiscoveryReadOnly" => "探测读取（不可恢复）",
        _ => "没有覆盖声明"
    };
}

public sealed class SnapshotSourceViewModel
{
    public SnapshotSourceViewModel(VideoSource source)
    {
        Name = source.Name;
        SourceKind = SnapshotParameterPresentation.SourceKindName(source.Kind);
        DeviceName = source.Device?.FriendlyName ?? "未记录采集设备";
        DeviceIdentity = FormatDeviceIdentity(source.Device);
        Resolution = source.Mode is null ? "未记录" : $"{source.Mode.Width} × {source.Mode.Height}";
        FrameRate = source.Mode is null
            ? "未记录"
            : $"{source.Mode.FramesPerSecondNumerator / (double)source.Mode.FramesPerSecondDenominator:0.##} FPS";
        PixelFormat = EmptyAsUnknown(source.Mode?.PixelFormat);
        ColorSpace = SnapshotParameterPresentation.ColorSpace(source.Mode?.ColorSpace);
        ColorRange = SnapshotParameterPresentation.ColorRange(source.Mode?.ColorRange);
        Settings = CreateSettings(SnapshotParameterPresentation.PresentSourceSettings(source.Settings));
        SettingsTitle = $"来源目标参数（{Settings.Count} 项）";
        Filters = source.Filters
            .OrderBy(filter => filter.Order)
            .Select(filter => new SnapshotFilterViewModel(filter))
            .ToArray();
        FilterSummary = Filters.Count == 0 ? "没有视频滤镜" : $"{Filters.Count} 个视频滤镜，按恢复顺序排列";
    }

    public string Name { get; }

    public string SourceKind { get; }

    public string DeviceName { get; }

    public string DeviceIdentity { get; }

    public string Resolution { get; }

    public string FrameRate { get; }

    public string PixelFormat { get; }

    public string ColorSpace { get; }

    public string ColorRange { get; }

    public string FilterSummary { get; }

    public IReadOnlyList<SnapshotSettingViewModel> Settings { get; }

    public string SettingsTitle { get; }

    public IReadOnlyList<SnapshotFilterViewModel> Filters { get; }

    public bool HasSettings => Settings.Count > 0;

    internal static IReadOnlyList<SnapshotSettingViewModel> CreateSettings(
        IReadOnlyList<PresentedSnapshotSetting> settings) => settings
        .Select(setting => new SnapshotSettingViewModel(
            setting.Name,
            setting.Value,
            setting.TechnicalName))
        .ToArray();

    private static string FormatDeviceIdentity(CaptureDeviceDescriptor? device)
    {
        if (device is null)
        {
            return "没有设备标识";
        }

        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.VendorId))
        {
            values.Add($"VID {device.VendorId}");
        }

        if (!string.IsNullOrWhiteSpace(device.ProductId))
        {
            values.Add($"PID {device.ProductId}");
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            values.Add($"序列号 {device.SerialNumber}");
        }

        return values.Count == 0 ? "没有可移植设备标识" : string.Join(" · ", values);
    }

    private static string EmptyAsUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "未记录" : value;
}

public sealed class SnapshotFilterViewModel
{
    public SnapshotFilterViewModel(VideoFilter filter)
    {
        Name = filter.Name;
        Kind = SnapshotParameterPresentation.FilterKindName(filter.Kind);
        TechnicalKind = filter.Kind;
        Order = $"第 {filter.Order + 1} 个";
        State = filter.Enabled ? "已启用" : "已停用";
        Settings = SnapshotSourceViewModel.CreateSettings(
            SnapshotParameterPresentation.PresentFilterSettings(filter.Settings));
        SettingsTitle = $"滤镜参数（{Settings.Count} 项）";
        Assets = filter.Assets.Select(asset => new SnapshotAssetViewModel(asset)).ToArray();
        Summary = $"{Order} · {State} · {Settings.Count} 项参数";
    }

    public string Name { get; }

    public string Kind { get; }

    public string TechnicalKind { get; }

    public string Order { get; }

    public string State { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotSettingViewModel> Settings { get; }

    public string SettingsTitle { get; }

    public IReadOnlyList<SnapshotAssetViewModel> Assets { get; }

    public bool HasSettings => Settings.Count > 0;

    public bool HasAssets => Assets.Count > 0;

}

public sealed class SnapshotAssetViewModel
{
    public SnapshotAssetViewModel(AssetBinding asset)
    {
        Name = asset.OriginalFileName;
        Detail = $"SHA-256 {asset.BlobSha256[..Math.Min(12, asset.BlobSha256.Length)]}…";
    }

    public string Name { get; }

    public string Detail { get; }

}

public sealed record SnapshotSettingViewModel(string Name, string Value, string TechnicalName);
