using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed class LocalMappingSourceItemViewModel(LocalMappingSource source)
{
    public Guid SourceLogicalId { get; } = source.SourceLogicalId;

    public ApplicationKind Application { get; } = source.Application;

    public string ApplicationName { get; } = source.Application == ApplicationKind.Obs
        ? "OBS"
        : "直播伴侣";

    public string SourceName { get; } = source.SourceName;

    public string DeviceName { get; } = source.DeviceName;

    public string RequiredMode { get; } = FormatMode(source.RequiredMode);

    public DeviceMapping? Mapping { get; } = source.Mapping;

    public string MappingStatus { get; } = source.Mapping is null
        ? "未映射"
        : $"已映射到 {source.Mapping.TargetSourceName}";

    private static string FormatMode(VideoMode? mode) => mode is null
        ? "未记录画面格式"
        : $"{mode.Width}×{mode.Height} · {mode.FramesPerSecondNumerator / (double)mode.FramesPerSecondDenominator:0.##} FPS · {mode.PixelFormat}";
}

public sealed class LocalMappingTargetItemViewModel(LocalMappingTarget target)
{
    public ApplicationKind Application { get; } = target.Application;

    public string SourceName { get; } = target.SourceName;

    public string TargetDeviceId { get; } = target.TargetDeviceId;

    public string DeviceName { get; } = target.DeviceName;

    public string CurrentMode { get; } = target.CurrentMode is null
        ? "未识别画面格式"
        : $"{target.CurrentMode.Width}×{target.CurrentMode.Height} · "
            + $"{target.CurrentMode.FramesPerSecondNumerator / (double)target.CurrentMode.FramesPerSecondDenominator:0.##} FPS · "
            + target.CurrentMode.PixelFormat;
}
