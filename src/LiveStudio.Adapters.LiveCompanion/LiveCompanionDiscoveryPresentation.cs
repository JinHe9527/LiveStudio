using System.Globalization;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal sealed record LiveCompanionDiscoveryFieldPresentation(
    string NativeName,
    string UiPath,
    string Category);

/// <summary>
/// Projects discovery-only native fields into the menu hierarchy shipped by Live Companion 12.8.1.
/// The projection is presentation evidence only: it never grants write access or raises evidence status.
/// </summary>
internal static class LiveCompanionDiscoveryPresentation
{
    public static LiveCompanionDiscoveryFieldPresentation Present(
        NativeConfigurationDocument document,
        NativeConfigurationValue value)
    {
        var fileName = document.RelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? document.RelativePath;
        var segments = Segments(value.JsonPointer);
        return fileName switch
        {
            "effectConfigStore.json" => PresentEffectConfiguration(document, value, segments),
            "sourceStore.json" => PresentSource(document, value, segments),
            "filterStore.json" => PresentFilterStore(value, segments),
            "effectStore.json" => PresentEffectState(value, segments),
            _ => Create("导入导出", "其他原生配置", TranslateLeaf(segments.LastOrDefault()), value.Category)
        };
    }

    private static LiveCompanionDiscoveryFieldPresentation PresentEffectConfiguration(
        NativeConfigurationDocument document,
        NativeConfigurationValue value,
        string[] segments)
    {
        var configsIndex = IndexOf(segments, "configs");
        if (configsIndex < 0 || configsIndex + 2 >= segments.Length)
        {
            return Create(
                "美颜设置",
                "配置关联",
                TranslateLeaf(segments.LastOrDefault()),
                value.Category);
        }

        var panel = segments[configsIndex + 2];
        var (section, group) = panel switch
        {
            "effect" or "effectall" or "picocomposer" => ("美颜设置", "美颜"),
            "slim" or "picoslim" => ("美颜设置", "美体"),
            "makeupstyle" or "picomakeup" => ("美妆设置", "风格整妆"),
            "makeupsingle" or "picomakeupsingle" => ("美妆设置", "自定义美妆"),
            "composerfilter" or "picofilter" => ("滤镜设置", "风格滤镜"),
            "default" => ("特效道具", "特效道具"),
            "interact" => ("特效道具", "互动特效"),
            "zongyiyouxi" => ("特效道具", "道具特效"),
            "matting" => ("绿幕抠图", "背景设置"),
            "lens-effect" => ("镜头特效", "运镜效果"),
            "atmosphere" => ("镜头特效", "镜头氛围"),
            _ => ("特效道具", "其他官方效果面板")
        };
        var scope = IndexOf(segments, "temps") >= 0 ? "预设参数" : "当前参数";
        var officialName = LiveCompanionOfficialDisplay.ParameterName(value.JsonPointer);
        var leafName = TranslateLeaf(segments.LastOrDefault());
        var name = officialName is null ? leafName : $"{officialName} · {leafName}";
        return Create(section, $"{group}/{scope}", name, value.Category);
    }

    private static LiveCompanionDiscoveryFieldPresentation PresentSource(
        NativeConfigurationDocument document,
        NativeConfigurationValue value,
        string[] segments)
    {
        var filterIndex = IndexOf(segments, "filterDataList");
        if (filterIndex >= 0 && filterIndex + 1 < segments.Length)
        {
            var itemNumber = DisplayIndex(segments[filterIndex + 1]);
            var filterName = FilterName(document, segments, filterIndex);
            var filterLabel = string.IsNullOrWhiteSpace(filterName)
                ? $"第 {itemNumber} 个滤镜"
                : $"第 {itemNumber} 个滤镜 · {filterName}";
            var curvesIndex = IndexOf(segments, "curves");
            if (curvesIndex >= 0 && curvesIndex + 1 < segments.Length)
            {
                var curveName = CurveName(document, value.JsonPointer, segments, curvesIndex);
                var anchorIndex = IndexOf(segments, "anchors");
                var anchor = anchorIndex >= 0 && anchorIndex + 1 < segments.Length
                    ? $"/控制点 {DisplayIndex(segments[anchorIndex + 1])}"
                    : string.Empty;
                var control = CurveControlName(segments);
                var leaf = TranslateLeaf(segments.LastOrDefault());
                var suffix = string.Join(" · ", new[] { control, leaf }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                return Create(
                    "滤镜设置",
                    $"视频滤镜链/{filterLabel}/曲线/{curveName}{anchor}",
                    suffix,
                    value.Category);
            }

            var itemName = TranslateLeaf(segments.LastOrDefault());
            return Create(
                "滤镜设置",
                $"视频滤镜链/{filterLabel}",
                itemName,
                value.Category);
        }

        if (IndexOf(segments, "filterData") >= 0)
        {
            return Create(
                "滤镜设置",
                "视频滤镜链",
                TranslateLeaf(segments.LastOrDefault()),
                value.Category);
        }

        if (IndexOf(segments, "effect1") >= 0 || segments.Contains("effectConfigId", StringComparer.Ordinal))
        {
            return Create(
                "美颜设置",
                "来源效果关联",
                TranslateLeaf(segments.LastOrDefault()),
                value.Category);
        }

        var fieldName = TranslateLeaf(segments.LastOrDefault());
        var inPayload = segments.Contains("payload", StringComparer.Ordinal);
        if (segments.LastOrDefault() == "name")
        {
            fieldName = inPayload ? "设备名称" : "来源名称";
        }

        var modeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "deviceId", "name", "width", "height", "rate", "format", "colorSpace", "videoRange"
        };
        var group = inPayload
                    && segments.LastOrDefault() is { } lastSegment
                    && modeFields.Contains(lastSegment)
            ? "设备与视频模式"
            : "视频来源";
        return Create("基础设置", group, fieldName, value.Category);
    }

    private static LiveCompanionDiscoveryFieldPresentation PresentFilterStore(
        NativeConfigurationValue value,
        string[] segments)
    {
        var templateIndex = IndexOf(segments, "filterTempList");
        var group = templateIndex >= 0 && templateIndex + 1 < segments.Length
            ? $"视频滤镜预设/第 {DisplayIndex(segments[templateIndex + 1])} 组"
            : "视频滤镜预设";
        return Create(
            "滤镜设置",
            group,
            TranslateLeaf(segments.LastOrDefault()),
            value.Category);
    }

    private static LiveCompanionDiscoveryFieldPresentation PresentEffectState(
        NativeConfigurationValue value,
        string[] segments)
    {
        var path = string.Join('/', segments);
        var (section, group) = path switch
        {
            _ when path.Contains("deviceFacing", StringComparison.Ordinal) => ("基础设置", "镜头方向"),
            _ when path.Contains("camera-mattingSetting", StringComparison.Ordinal) => ("绿幕抠图", "提示状态"),
            _ when path.Contains("sourceUsingLens", StringComparison.Ordinal)
                   || path.Contains("useCurveFilter", StringComparison.Ordinal) => ("镜头特效", "运行状态"),
            _ when path.Contains("giftPlayConfig", StringComparison.Ordinal) => ("特效道具", "礼物特效播放"),
            _ when path.Contains("carnivalInfo", StringComparison.Ordinal) => ("特效道具", "活动特效"),
            _ when path.Contains("shownTips", StringComparison.Ordinal) => ("特效道具", "提示状态"),
            _ => ("特效道具", "运行状态")
        };
        return Create(section, group, TranslateLeaf(segments.LastOrDefault()), value.Category);
    }

    private static LiveCompanionDiscoveryFieldPresentation Create(
        string section,
        string group,
        string name,
        string category) => new(name, $"{section}/{group}/{name}", category);

    private static string CurveName(
        NativeConfigurationDocument document,
        string pointer,
        string[] segments,
        int curvesIndex)
    {
        if (curvesIndex + 2 < segments.Length)
        {
            var branch = segments[curvesIndex + 2];
            var channel = branch switch
            {
                "lum" => "Master",
                "red" => "R",
                "green" => "G",
                "blue" => "B",
                _ => null
            };
            if (channel is not null)
            {
                return channel;
            }
        }

        var typePointer = string.Join('/', pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Take(curvesIndex + 2));
        typePointer = $"/{typePointer}/type";
        var type = document.Values.FirstOrDefault(candidate => candidate.JsonPointer == typePointer)?.Value;
        return type is { ValueKind: System.Text.Json.JsonValueKind.Number }
               && type.Value.TryGetInt32(out var number)
            ? number switch
            {
                0 => "Master、R、G、B",
                1 => "色相对色相",
                2 => "色相对饱和度",
                3 => "色相对明度",
                4 => "明度对饱和度",
                5 => "饱和度对饱和度",
                6 => "饱和度对明度",
                _ => $"曲线通道 {number + 1}"
            }
            : $"曲线通道 {DisplayIndex(segments[curvesIndex + 1])}";
    }

    private static string CurveControlName(string[] segments)
    {
        if (segments.Contains("leftControl", StringComparer.Ordinal))
        {
            return "左控制柄";
        }

        if (segments.Contains("rightControl", StringComparer.Ordinal))
        {
            return "右控制柄";
        }

        return segments.Contains("position", StringComparer.Ordinal) ? "位置" : string.Empty;
    }

    private static string? FilterName(
        NativeConfigurationDocument document,
        string[] segments,
        int filterIndex)
    {
        var pointer = "/" + string.Join('/', segments
            .Take(filterIndex + 2)
            .Select(EscapePointerSegment)) + "/name";
        var value = document.Values.FirstOrDefault(candidate => candidate.JsonPointer == pointer)?.Value;
        return value is { ValueKind: System.Text.Json.JsonValueKind.String }
            ? value.Value.GetString()
            : null;
    }

    private static string TranslateLeaf(string? leaf) => leaf switch
    {
        null or "" => "原生字段",
        "use" => "启用",
        "absVal" => "原生值",
        "value" => "界面值",
        "panelUse" => "面板启用",
        "name" => "名称",
        "type" => "类型",
        "id" => "内部标识",
        "configId" => "配置标识",
        "effectConfigId" => "美颜配置关联",
        "mergeId" => "合并标识",
        "edition" => "版本",
        "deviceId" => "视频设备",
        "width" => "分辨率宽度",
        "height" => "分辨率高度",
        "rate" => "帧率",
        "format" => "像素格式",
        "colorSpace" => "色彩空间",
        "videoRange" => "色彩范围",
        "enable" => "启用",
        "filterDataList" => "视频滤镜链",
        "filterInstanceList" => "当前滤镜实例",
        "filterTempList" => "滤镜预设",
        "resourceFilePath" => "滤镜素材目录",
        "showRedDot" => "新功能提示",
        "useFilterSuppor" => "滤镜能力版本",
        "useNewFilter" => "新版滤镜引擎",
        "amount" => "强度",
        "brightness" => "亮度",
        "contrast" => "对比度",
        "gamma" => "伽马",
        "saturation" => "饱和度",
        "hueshift" => "色相偏移",
        "opacity" => "不透明度",
        "radius" => "半径",
        "sigma" => "模糊范围",
        "cct" => "色温",
        "tint" => "色调",
        "colorAdd" => "叠加颜色",
        "colorMultiply" => "混合颜色",
        "selectedHueKey" => "选中色相",
        "hue" => "色相",
        "hueAdjust" => "色相调整",
        "saturationAdjust" => "饱和度调整",
        "lightnessAdjust" => "明度调整",
        "file" or "fileName" => "素材文件",
        "curvesType" => "曲线模式",
        "enableRenderFilter" => "礼物画面滤镜",
        "enableSpeech" => "语音识别",
        "queueLength" => "播放队列长度",
        "sourceTransparency" => "来源透明度",
        "preDuration" => "预加载时长",
        "groupEffectIds" => "礼物特效分组",
        "sourceCanvasShowRate" => "画布显示比例",
        "sourceSelfShowRate" => "本地显示比例",
        "showChat" => "聊天显示",
        "sourceUsingLens" => "来源镜头效果",
        "useCurveFilter" => "曲线滤镜开关",
        "deviceFacing" => "摄像头方向记录",
        "liveScene" => "直播画面来源",
        "secondSource" => "第二视频来源",
        "viewIndex" => "来源顺序",
        "useLoki" => "效果引擎开关",
        "x" => "横坐标",
        "y" => "纵坐标",
        _ when int.TryParse(leaf, NumberStyles.None, CultureInfo.InvariantCulture, out var index) =>
            $"第 {index + 1} 项",
        _ => "内部参数"
    };

    private static int IndexOf(string[] segments, string expected)
    {
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Equals(expected, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string DisplayIndex(string segment) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? (index + 1).ToString(CultureInfo.InvariantCulture)
            : "?";

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal))
        .ToArray();

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
