namespace LiveStudio.Contracts;

/// <summary>
/// 直播伴侣 12.8.1.454484231 自带资源中的官方中文参数名。
/// 这些名称只改善展示，不改变字段证据等级、写入权限或恢复兼容性。
/// </summary>
public static class LiveCompanionOfficialDisplay
{
    private static readonly Dictionary<string, string> ParameterNames =
        new(StringComparer.Ordinal)
        {
            ["6938367737173381646|BigEye"] = "大眼",
            ["6938367827954897439|Clear_ALL"] = "清晰",
            ["6938368504076702239|Internal_Deform_CutFace"] = "窄脸",
            ["6938369649906029070|Internal_Deform_Face"] = "小脸",
            ["6938369757032747527|Internal_Deform_Zoom_Cheekbone"] = "瘦颧骨",
            ["6938369845566116383|Internal_Deform_Zoom_Jawbone"] = "瘦下颌骨",
            ["6938369911022424612|Internal_Deform_Nose"] = "瘦鼻",
            ["6938369999706788360|Internal_Deform_MovNose"] = "长鼻",
            ["6938370064194212389|Mouth_ALL"] = "嘴形",
            ["6938370126815171108|Internal_Deform_Chin"] = "下巴",
            ["6938370225481978405|Internal_Deform_Forehead"] = "额头",
            ["6938370384030863908|removePouch"] = "黑眼圈",
            ["6938370510002590222|removeNasolabialFolds"] = "法令纹",
            ["7108615070787047973|Foundation_ALL"] = "美白",
            ["7182122554402804281|Face_all"] = "自然脸",
            ["7182123383063056933|Smooth_ALL"] = "磨皮",
            ["7321632612093530651|Teeth_ALL"] = "白牙",
            ["7322286931461542409|SmallHead"] = "小头",
            ["7322287526155129382|SlimWaist"] = "瘦腰",
            ["7322287672976740874|SlimHip"] = "美胯",
            ["7322287756384670245|SlimArm"] = "瘦手臂",
            ["7322287820700127754|SwanNeck"] = "天鹅颈",
            ["7322287893517439497|SlimBody"] = "瘦身",
            ["7322287963704922651|WidenShoulder"] = "肩宽",
            ["7322288040540377651|OrthoShoulder"] = "直角肩",
            ["7369406873918771749|Internal_Deform_MovMouth"] = "人中",
            ["7369407055687324211|Internal_Deform_MoveEye"] = "眼睛上下",
            ["7369407133349057075|Internal_Eye_Spacing"] = "眼睛距离",
            ["7369407217197388297|fd_lower_eyelid"] = "眼睑下至",
            ["7369407491056079387|fd_pupil"] = "瞳孔大小",
            ["7369407562321498651|fd_nose_root"] = "山根",
            ["7369407704319660571|fd_nose_size"] = "鼻翼",
            ["7369407776868536841|fd_nose_bridge_v6"] = "鼻梁",
            ["7369407978341929498|fd_brow_size_v6"] = "眉毛粗细",
            ["7369408148454511155|fd_brow_position_v6"] = "眉毛高低",
            ["7372079818403222043|fd_inner_corner_v6"] = "内眼角",
            ["7372079895343534630|fd_outer_corner_inout_v6"] = "外眼角",
            ["7372459465762673161|fd_mouse_corner_under_lip_line_v6"] = "微笑唇",
            ["7532038335389241883|Face_all_live_left"] = "瘦左脸",
            ["7532038401877348902|Face_all_live_right"] = "瘦右脸",
            ["7532038463160324654|BigEye_live_left"] = "左眼",
            ["7532038535994413595|BigEye_live_right"] = "右眼",
            ["7548795774864200219|smaller_head_v8"] = "头包脸",
            ["7633298414997869106|fd_proportion_upper_atrium_v6"] = "上庭",
            ["7633298500486173211|banlv_zhongting_v8"] = "中庭",
            ["7633298570791096859|fd_proportion_lower_atrium_v6"] = "下庭",
            ["7633298774382613019|banlv_etougaodu_v8"] = "额高",
            ["7633298858268693001|banlv_etoukuandu_v8"] = "额宽",
            ["7633298925012652582|banlv_taiyangxue_v8"] = "太阳穴",
            ["7633304109646352906|fd_brow_distance_v6"] = "眉距",
            ["7633628054253736498|Foundation_ALL"] = "基础",
            ["7633628816044200498|Smooth_ALL"] = "磨皮",
            ["7633629244530102810|Clear_ALL"] = "清晰",
            ["7633629577125827110|removePouch"] = "黑眼圈",
            ["7633629696499913267|removeNasolabialFolds"] = "法令纹",
            ["7636682380756914714|banlv_mchun_v8"] = "M唇",
            ["7639369841605874202|banlv_jianxiaba_v8"] = "整体"
        };

    public static string? ParameterName(string technicalPath)
    {
        var segments = technicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Unescape)
            .ToArray();
        var itemIndex = Array.IndexOf(segments, "items");
        var sliderIndex = Array.IndexOf(segments, "sliders");
        if (itemIndex < 0 || itemIndex + 1 >= segments.Length
            || sliderIndex < 0 || sliderIndex + 1 >= segments.Length)
        {
            return null;
        }

        return ParameterNames.TryGetValue(
            $"{segments[itemIndex + 1]}|{segments[sliderIndex + 1]}",
            out var name)
            ? name
            : null;
    }

    private static string Unescape(string value) => value.Replace("~1", "/", StringComparison.Ordinal)
        .Replace("~0", "~", StringComparison.Ordinal);
}
