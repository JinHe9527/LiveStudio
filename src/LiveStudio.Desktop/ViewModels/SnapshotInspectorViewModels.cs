using System.Globalization;
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
    }

    public string Name { get; }

    public string CreatedAt { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotApplicationViewModel> Applications { get; }

    public string Verification { get; }

    public string VerificationDetail { get; }

    public bool HasVerification => !string.IsNullOrWhiteSpace(Verification);
}

public sealed class SnapshotApplicationViewModel
{
    public SnapshotApplicationViewModel(ApplicationSnapshot application)
    {
        Name = application.Kind == ApplicationKind.Obs ? "OBS Studio" : "抖音直播伴侣";
        Version = $"版本 {application.Version}";
        Sources = application.Sources.Select(source => new SnapshotSourceViewModel(source)).ToArray();
        var nativeFieldCount = application.NativeDocuments.Sum(document => document.Values.Count);
        Summary = nativeFieldCount == 0
            ? $"{Sources.Count} 个视频来源 · {Sources.Sum(source => source.Filters.Count)} 个滤镜"
            : $"{Sources.Count} 个视频来源 · {Sources.Sum(source => source.Filters.Count)} 个滤镜 · {nativeFieldCount} 项原生字段已完整保存";
    }

    public string Name { get; }

    public string Version { get; }

    public string Summary { get; }

    public IReadOnlyList<SnapshotSourceViewModel> Sources { get; }
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
        SettingsTitle = $"完整来源参数（{Settings.Count} 项）";
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
    public SnapshotAssetViewModel(AssetReference asset)
    {
        Name = asset.OriginalFileName;
        Detail = $"{FormatSize(asset.Length)} · SHA-256 {asset.Sha256[..Math.Min(12, asset.Sha256.Length)]}…";
    }

    public string Name { get; }

    public string Detail { get; }

    private static string FormatSize(long length) => length switch
    {
        >= 1024L * 1024L => $"{length / (1024d * 1024d):0.0} MB",
        >= 1024L => $"{length / 1024d:0.0} KB",
        _ => $"{length} B"
    };
}

public sealed record SnapshotSettingViewModel(string Name, string Value, string TechnicalName);
