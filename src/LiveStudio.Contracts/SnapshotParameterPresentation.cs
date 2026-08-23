using System.Text.Json;

namespace LiveStudio.Contracts;

public sealed record PresentedSnapshotSetting(string Name, string Value, string TechnicalName);

public static class SnapshotParameterPresentation
{
    private static readonly Dictionary<string, string> SourceSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video_device_id"] = "设备接口标识",
        ["video_device_name"] = "设备名称",
        ["res_type"] = "分辨率模式",
        ["resolution"] = "分辨率",
        ["frame_interval"] = "帧间隔",
        ["fps_num"] = "帧率分子",
        ["fps_den"] = "帧率分母",
        ["fps"] = "帧率",
        ["video_format"] = "像素格式",
        ["color_space"] = "色彩空间",
        ["color_range"] = "色彩范围",
        ["buffering"] = "缓冲模式",
        ["cameraId"] = "设备接口标识",
        ["captureDevice"] = "采集设备",
        ["deviceId"] = "设备接口标识",
        ["frameRate"] = "帧率",
        ["pixelFormat"] = "像素格式",
        ["colorSpace"] = "色彩空间",
        ["colorRange"] = "色彩范围"
    };

    private static readonly Dictionary<string, string> FilterSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amount"] = "强度",
        ["brightness"] = "亮度",
        ["contrast"] = "对比度",
        ["saturation"] = "饱和度",
        ["gamma"] = "伽马",
        ["hue_shift"] = "色相偏移",
        ["opacity"] = "不透明度",
        ["sharpness"] = "锐化强度",
        ["image_path"] = "素材文件",
        ["path"] = "素材文件",
        ["similarity"] = "相似度",
        ["smoothness"] = "平滑度",
        ["spill"] = "溢色抑制",
        ["key_color"] = "抠像颜色",
        ["left"] = "左侧裁切",
        ["top"] = "顶部裁切",
        ["right"] = "右侧裁切",
        ["bottom"] = "底部裁切",
        ["relative"] = "相对裁切",
        ["cx"] = "宽度",
        ["cy"] = "高度",
        ["resolution"] = "目标分辨率",
        ["sampling"] = "缩放算法",
        ["speed_x"] = "水平速度",
        ["speed_y"] = "垂直速度",
        ["loop"] = "循环",
        ["limit_width"] = "限制宽度",
        ["limit_height"] = "限制高度",
        ["curve"] = "曲线",
        ["curves"] = "曲线集",
        ["points"] = "控制点",
        ["controlPoints"] = "控制点",
        ["x"] = "X 坐标",
        ["y"] = "Y 坐标",
        ["channel"] = "颜色通道",
        ["interpolation"] = "插值方式",
        ["red"] = "红色通道",
        ["green"] = "绿色通道",
        ["blue"] = "蓝色通道",
        ["master"] = "主通道"
    };

    public static IReadOnlyList<PresentedSnapshotSetting> PresentSourceSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => PresentSettings(settings, SourceSettingNames);

    public static IReadOnlyList<PresentedSnapshotSetting> PresentFilterSettings(
        IReadOnlyDictionary<string, JsonElement> settings) => PresentSettings(settings, FilterSettingNames);

    public static string PresentTechnicalName(string name)
    {
        if (SourceSettingNames.TryGetValue(name, out var sourceName))
        {
            return sourceName;
        }

        return FilterSettingNames.TryGetValue(name, out var filterName)
            ? filterName
            : name.Replace('_', ' ');
    }

    public static string SourceKindName(string kind) => kind switch
    {
        "dshow_input" or "av_capture_input" => "视频采集设备",
        "live_companion_video_source" => "直播伴侣视频来源",
        _ => kind
    };

    public static string FilterKindName(string kind) => kind switch
    {
        "clut_filter" or "color_grade_filter" => "LUT 调色",
        "color_filter" or "color_filter_v2" => "色彩校正",
        "sharpness_filter" or "sharpness_filter_v2" => "锐化",
        "chroma_key_filter" or "chroma_key_filter_v2" => "色度键",
        "color_key_filter" or "color_key_filter_v2" => "颜色键",
        "crop_filter" => "裁切/填充",
        "mask_filter" or "mask_filter_v2" => "图像遮罩/混合",
        "scale_filter" => "缩放/宽高比",
        "scroll_filter" => "滚动",
        "luma_key_filter" or "luma_key_filter_v2" => "亮度键",
        "hdr_tonemap_filter" => "HDR 色调映射",
        "render_delay" or "render_delay_filter" => "渲染延迟",
        "gpu_delay" => "异步延迟",
        "live_companion_filter" => "直播伴侣滤镜",
        _ => "视频滤镜"
    };

    public static string ColorSpace(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "709" or "REC.709" => "Rec.709",
        "601" or "REC.601" => "Rec.601",
        "SRGB" => "sRGB",
        _ => EmptyAsUnknown(value)
    };

    public static string ColorRange(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "FULL" => "全范围（Full）",
        "PARTIAL" or "LIMITED" => "有限范围（Partial）",
        _ => EmptyAsUnknown(value)
    };

    private static PresentedSnapshotSetting[] PresentSettings(
        IReadOnlyDictionary<string, JsonElement> settings,
        Dictionary<string, string> names) => settings
        .OrderBy(setting => setting.Key, StringComparer.Ordinal)
        .Select(setting =>
        {
            var lookupName = setting.Key.Split('/').LastOrDefault() ?? setting.Key;
            return new PresentedSnapshotSetting(
            names.TryGetValue(lookupName, out var name) ? name : lookupName.Replace('_', ' '),
            FormatSettingValue(setting.Key, setting.Value),
            setting.Key);
        })
        .ToArray();

    private static string FormatSettingValue(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number
            && IsNormalizedSetting(name)
            && value.TryGetDouble(out var number))
        {
            return $"{value}（{number * 100:0.##}%）";
        }

        if (value.ValueKind == JsonValueKind.String
            && name.Contains("path", StringComparison.OrdinalIgnoreCase)
            && value.GetString() is { Length: > 0 } path)
        {
            return $"{Path.GetFileName(path)} · {path}";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "空",
            _ => value.ToString()
        };
    }

    private static bool IsNormalizedSetting(string name) => name is
        "amount" or
        "brightness" or
        "contrast" or
        "gamma" or
        "opacity" or
        "saturation" or
        "sharpness" or
        "similarity" or
        "smoothness" or
        "spill";

    private static string EmptyAsUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "未记录" : value;
}
