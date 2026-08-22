namespace LiveStudio.Contracts;

public sealed record SnapshotParameterDifference(string Label, string LeftValue, string RightValue);

public static class SnapshotParameterComparer
{
    public static IReadOnlyList<SnapshotParameterDifference> Compare(
        SnapshotDetail left,
        SnapshotDetail right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftValues = Flatten(left);
        var rightValues = Flatten(right);
        return leftValues.Keys
            .Union(rightValues.Keys, StringComparer.Ordinal)
            .Select(key =>
            {
                leftValues.TryGetValue(key, out var leftValue);
                rightValues.TryGetValue(key, out var rightValue);
                return new SnapshotParameterDifference(
                    leftValue?.Label ?? rightValue?.Label ?? key,
                    leftValue?.Value ?? "—",
                    rightValue?.Value ?? "—");
            })
            .Where(value => !string.Equals(value.LeftValue, value.RightValue, StringComparison.Ordinal))
            .OrderBy(value => value.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static Dictionary<string, SnapshotParameterValue> Flatten(SnapshotDetail detail)
    {
        var values = new Dictionary<string, SnapshotParameterValue>(StringComparer.Ordinal);
        foreach (var application in detail.Applications)
        {
            var applicationKey = application.Kind.ToString();
            var applicationName = ApplicationName(application.Kind);
            Add(values, $"{applicationKey}/version", $"{applicationName} / 版本", application.Version);
            foreach (var source in application.Sources)
            {
                var sourceKey = $"{applicationKey}/source/{source.LogicalId:N}";
                var sourceLabel = $"{applicationName} / {source.Name}";
                Add(values, $"{sourceKey}/name", $"{sourceLabel} / 来源名称", source.Name);
                Add(values, $"{sourceKey}/device", $"{sourceLabel} / 采集设备", FormatDevice(source.Device));
                Add(values, $"{sourceKey}/mode", $"{sourceLabel} / 画面模式", FormatMode(source.Mode));
                foreach (var setting in SnapshotParameterPresentation.PresentSourceSettings(source.Settings))
                {
                    Add(
                        values,
                        $"{sourceKey}/setting/{setting.TechnicalName}",
                        $"{sourceLabel} / {setting.Name}",
                        setting.Value);
                }

                foreach (var filter in source.Filters)
                {
                    var filterKey = $"{sourceKey}/filter/{filter.LogicalId:N}";
                    var filterLabel = $"{sourceLabel} / 滤镜 {filter.Name}";
                    Add(values, $"{filterKey}/name", $"{filterLabel} / 名称", filter.Name);
                    Add(
                        values,
                        $"{filterKey}/kind",
                        $"{filterLabel} / 类型",
                        $"{SnapshotParameterPresentation.FilterKindName(filter.Kind)}（{filter.Kind}）");
                    Add(values, $"{filterKey}/enabled", $"{filterLabel} / 状态", filter.Enabled ? "启用" : "停用");
                    Add(
                        values,
                        $"{filterKey}/order",
                        $"{filterLabel} / 顺序",
                        (filter.Order + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var setting in SnapshotParameterPresentation.PresentFilterSettings(filter.Settings))
                    {
                        Add(
                            values,
                            $"{filterKey}/setting/{setting.TechnicalName}",
                            $"{filterLabel} / {setting.Name}",
                            setting.Value);
                    }

                    Add(
                        values,
                        $"{filterKey}/assets",
                        $"{filterLabel} / 素材",
                        string.Join(
                            ", ",
                            filter.Assets
                                .OrderBy(asset => asset.OriginalFileName, StringComparer.Ordinal)
                                .Select(asset => $"{asset.OriginalFileName}（{asset.Sha256}）")));
                }
            }

            foreach (var document in application.NativeDocuments)
            {
                foreach (var value in document.Values)
                {
                    Add(
                        values,
                        $"{applicationKey}/native/{document.StoreId}/{document.RelativePath}/{value.JsonPointer}",
                        $"{applicationName} / {document.RelativePath} / {NativeParameterName(value)}",
                        NativeParameterValue(value.Value));
                }
            }
        }

        return values;
    }

    private static void Add(
        IDictionary<string, SnapshotParameterValue> values,
        string key,
        string label,
        string value) => values[key] = new SnapshotParameterValue(label, value);

    private static string ApplicationName(ApplicationKind application) => application == ApplicationKind.Obs
        ? "OBS"
        : "直播伴侣";

    private static string FormatMode(VideoMode? mode) => mode is null
        ? "未记录画面格式"
        : $"{mode.Width}×{mode.Height} · {mode.FramesPerSecondNumerator / (double)mode.FramesPerSecondDenominator:0.##} FPS · {mode.PixelFormat} · {SnapshotParameterPresentation.ColorSpace(mode.ColorSpace)} · {SnapshotParameterPresentation.ColorRange(mode.ColorRange)}";

    private static string FormatDevice(CaptureDeviceDescriptor? device) => device is null
        ? "未记录"
        : string.Join(
            " · ",
            new[] { device.FriendlyName, device.VendorId, device.ProductId, device.SerialNumber }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string NativeParameterName(NativeConfigurationValue value)
    {
        var technicalName = value.JsonPointer.Split('/').LastOrDefault() ?? value.JsonPointer;
        return value.Category switch
        {
            "DeviceSelection" => $"采集设备（{technicalName}）",
            "VideoMode" => $"画面参数（{technicalName}）",
            "Filter" => $"滤镜配置（{technicalName}）",
            _ => technicalName
        };
    }

    private static string NativeParameterValue(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? string.Empty,
        System.Text.Json.JsonValueKind.True => "是",
        System.Text.Json.JsonValueKind.False => "否",
        _ => value.ToString()
    };

    private sealed record SnapshotParameterValue(string Label, string Value);
}
