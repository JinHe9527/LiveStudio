using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Desktop.Controls;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotInspectorViewModelTests
{
    [Fact]
    public void CameraTabKeepsLastApplicationAvailableForTechnicalPanel()
    {
        var application = CreateApplication(ApplicationKind.Obs);
        var inspector = new SnapshotInspectorViewModel(
            "2026-08-27 01:00",
            DateTimeOffset.Now,
            [application]);
        var selected = inspector.SelectedApplication;

        inspector.SelectCameraCommand.Execute(null);

        Assert.True(inspector.IsCameraSelected);
        Assert.False(inspector.IsApplicationSelected);
        Assert.Null(inspector.SelectedApplication);
        Assert.Same(selected, inspector.TechnicalApplication);
    }

    [Theory]
    [InlineData(680, 205, 5, 57, 3)]
    [InlineData(1340, 205, 5, 57, 5)]
    [InlineData(180, 205, 5, 57, 1)]
    [InlineData(1920, 205, 5, 2, 2)]
    public void AdaptiveParameterPanelUsesAvailableWidth(
        double width,
        double minimumItemWidth,
        int maximumColumns,
        int itemCount,
        int expectedColumns)
    {
        Assert.Equal(
            expectedColumns,
            AdaptiveUniformPanel.CalculateColumnCount(width, minimumItemWidth, maximumColumns, itemCount));
    }

    [Theory]
    [InlineData(519, 1)]
    [InlineData(1040, 2)]
    [InlineData(1920, 2)]
    public void CurveGroupsKeepWideRowsWhenWindowWidthChanges(double width, int expectedColumns)
    {
        var group = new SnapshotFieldGroupViewModel("第 6 个滤镜 · 曲线", []);

        Assert.True(group.IsCurveGroup);
        Assert.Equal(520, group.FieldMinItemWidth);
        Assert.Equal(2, group.FieldMaxColumns);
        Assert.Equal(
            expectedColumns,
            AdaptiveUniformPanel.CalculateColumnCount(
                width,
                group.FieldMinItemWidth,
                group.FieldMaxColumns,
                itemCount: 3));
    }

    [Fact]
    public void LiveCompanionMenuOnlyKeepsGroupsWithEffectiveContent()
    {
        var values = new[]
        {
            new NativeConfigurationValue("/device/width", "Discovery", JsonSerializer.SerializeToElement(1920)),
            new NativeConfigurationValue("/filters/0/strength", "Discovery", JsonSerializer.SerializeToElement(20)),
            new NativeConfigurationValue("/chroma/use", "Discovery", JsonSerializer.SerializeToElement(false)),
            new NativeConfigurationValue("/chroma/strength", "Discovery", JsonSerializer.SerializeToElement(80)),
            new NativeConfigurationValue("/effectStore/giftPlayConfig/enableRenderFilter", "Discovery", JsonSerializer.SerializeToElement(true)),
            new NativeConfigurationValue("/effectStore/giftPlayConfig/preDuration", "Discovery", JsonSerializer.SerializeToElement(7.2)),
            new NativeConfigurationValue("/effectStore/carnivalInfo/isOn", "Discovery", JsonSerializer.SerializeToElement(true)),
            new NativeConfigurationValue("/effectStore/carnivalInfo/using", "Discovery", JsonSerializer.SerializeToElement(false))
        };
        var document = new NativeConfigurationDocument(
            "source-store",
            "JsonFile",
            "1",
            "WBStore",
            "sourceStore.json",
            new string('a', 64),
            Guid.NewGuid(),
            values);
        var paths = new[]
        {
            ("/device/width", "基础设置/设备与视频模式/分辨率宽度", "分辨率宽度"),
            ("/filters/0/strength", "滤镜设置/视频滤镜链/第 1 个滤镜/强度", "强度"),
            ("/chroma/use", "绿幕抠图/抠图/启用", "启用"),
            ("/chroma/strength", "绿幕抠图/抠图/强度", "强度"),
            ("/effectStore/giftPlayConfig/enableRenderFilter", "特效道具/礼物特效播放/礼物画面滤镜", "礼物画面滤镜"),
            ("/effectStore/giftPlayConfig/preDuration", "特效道具/礼物特效播放/预加载时长", "预加载时长"),
            ("/effectStore/carnivalInfo/isOn", "特效道具/活动特效/能力状态", "能力状态"),
            ("/effectStore/carnivalInfo/using", "特效道具/活动特效/使用状态", "使用状态")
        };
        var fields = paths.Select(item => new CapturedParameterField(
            $"sourceStore.json:{item.Item1}",
            "Discovery",
            "Number",
            true,
            false,
            "DiscoveryReadOnly",
            NativeName: item.Item3,
            UiPath: item.Item2,
            EvidenceStatus: FieldEvidenceStatus.EvidenceOnly)).ToArray();
        var snapshot = new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            "12.8.1",
            "webcast-mate-json-discovery",
            string.Empty,
            new string('b', 64),
            CompatibilityLevel.Unsupported,
            true,
            fields,
            [],
            [document],
            CreateTree());

        var viewModel = new SnapshotApplicationViewModel(snapshot);

        Assert.Equal(["基础设置", "滤镜设置"], viewModel.NativeMenuGroups.Select(group => group.Name));
        Assert.Equal("基础设置", viewModel.SelectedParameterGroup?.Name);
    }

    [Fact]
    public void LiveCompanionMenuShowsSpecialEffectsOnlyWhenUserContentExists()
    {
        const string usePath = "/effectConfigStore/configs/current/default/use";
        const string strengthPath = "/effectConfigStore/configs/current/default/items/0/strength";
        var values = new[]
        {
            new NativeConfigurationValue(usePath, "Discovery", JsonSerializer.SerializeToElement(true)),
            new NativeConfigurationValue(strengthPath, "Discovery", JsonSerializer.SerializeToElement(24))
        };
        var document = new NativeConfigurationDocument(
            "effect-config", "JsonFile", "1", "WBStore", "effectConfigStore.json",
            new string('a', 64), Guid.NewGuid(), values);
        var fields = new[]
        {
            new CapturedParameterField(
                $"effectConfigStore.json:{usePath}", "Discovery", "Boolean", true, false,
                "DiscoveryReadOnly", NativeName: "启用", UiPath: "特效道具/画面特效/启用",
                EvidenceStatus: FieldEvidenceStatus.EvidenceOnly),
            new CapturedParameterField(
                $"effectConfigStore.json:{strengthPath}", "Discovery", "Number", true, false,
                "DiscoveryReadOnly", NativeName: "强度", UiPath: "特效道具/画面特效/强度",
                EvidenceStatus: FieldEvidenceStatus.EvidenceOnly)
        };
        var snapshot = new ApplicationSnapshot(
            ApplicationKind.LiveCompanion, "12.8.1", "webcast-mate-json-discovery", string.Empty,
            new string('b', 64), CompatibilityLevel.Unsupported, true, fields, [], [document], CreateTree());

        var viewModel = new SnapshotApplicationViewModel(snapshot);

        Assert.Contains(viewModel.NativeMenuGroups, group => group.Name == "特效道具");
    }

    [Fact]
    public void LiveCompanionDiscoveryIsPresentedAsNativeFieldsInsteadOfVideoSources()
    {
        var document = new NativeConfigurationDocument(
            "beauty",
            "JsonFile",
            "1",
            "beauty.json",
            "beauty.json",
            new string('a', 64),
            Guid.NewGuid(),
            [
                new NativeConfigurationValue(
                    "/smoothness",
                    "Filter",
                    JsonSerializer.SerializeToElement(0.45)),
                new NativeConfigurationValue(
                    "/unrecognizedInternalValue",
                    "Filter",
                    JsonSerializer.SerializeToElement(7))
            ]);
        var snapshot = new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            "12.8.1",
            "webcast-mate-json-discovery",
            string.Empty,
            new string('b', 64),
            CompatibilityLevel.Unsupported,
            true,
            document.Values.Select(value => new CapturedParameterField(
                $"beauty.json:{value.JsonPointer}",
                value.Category,
                value.Value.ValueKind.ToString(),
                true,
                false,
                "DiscoveryReadOnly",
                NativeName: value.JsonPointer.TrimStart('/'),
                UiPath: $"未映射原生字段/beauty.json/{value.JsonPointer.TrimStart('/')}",
                EvidenceStatus: FieldEvidenceStatus.EvidenceOnly)).ToArray(),
            [new VideoSource(Guid.NewGuid(), "beauty", "live_companion_video_source", null, null, new Dictionary<string, JsonElement>(), [])],
            [document],
            CreateTree());

        var viewModel = new SnapshotApplicationViewModel(snapshot);

        Assert.Empty(viewModel.Sources);
        Assert.Equal(1, viewModel.NativeDocumentCount);
        var nativeDocument = Assert.Single(viewModel.NativeDocuments);
        Assert.Same(nativeDocument, viewModel.SelectedNativeDocument);
        Assert.Equal("检测到的原生配置", nativeDocument.Name);
        Assert.Equal("完整保存 2 个原始字段", nativeDocument.Detail);
        Assert.Equal(2, nativeDocument.VisibleFields.Count);
        Assert.Equal("smoothness", nativeDocument.VisibleFields[0].Name);
        Assert.Equal("0.45", nativeDocument.VisibleFields[0].Value);
        Assert.Equal("EvidenceOnly · 待映射", nativeDocument.VisibleFields[0].Status);
        Assert.Equal(2, viewModel.ReadFieldCount);
        Assert.Equal(0, viewModel.VerifiedFieldCount);
        Assert.Contains("2 个有内容的配置项", viewModel.Summary, StringComparison.Ordinal);
        Assert.Equal(9, viewModel.FeatureGroups.Count);
        Assert.Equal(
            ["基础设置", "美颜设置", "美妆设置", "滤镜设置", "特效道具", "绿幕抠图", "镜头特效", "导入导出"],
            viewModel.FeatureGroups.Take(8).Select(group => group.Name));
        Assert.Contains(viewModel.FeatureGroups, group => group.Name == "待映射原生数据" && group.Count == 2);

        viewModel.SelectedParameterGroup = viewModel.ParameterGroups.Single(group => group.Key == "未映射原生字段");
        viewModel.FieldSearchText = "smoothness";

        var field = Assert.Single(viewModel.VisibleCoverageFields);
        Assert.Equal("smoothness", field.Name);
        Assert.Contains("当前只列 1 项", viewModel.CoverageSearchSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDocumentUsesReadableLabelsWithoutClaimingUiMapping()
    {
        var document = new NativeConfigurationDocument(
            "effect-config",
            "JsonFile",
            "1",
            "WBStore",
            @"WBStore\effectConfigStore.json",
            new string('a', 64),
            Guid.NewGuid(),
            [
                new NativeConfigurationValue(
                    "/effect/items/6938367737173381646/sliders/BigEye/absVal",
                    "Discovery",
                    JsonSerializer.SerializeToElement(-5)),
                new NativeConfigurationValue(
                    "/effect/items/6938367737173381646/sliders/BigEye/use",
                    "Discovery",
                    JsonSerializer.SerializeToElement(true))
            ]);

        var viewModel = new SnapshotNativeDocumentViewModel(document);

        Assert.Equal("检测到的效果配置", viewModel.Name);
        Assert.Equal("effectConfigStore.json", viewModel.FileName);
        Assert.Equal("内部字段 BigEye · 界面数值（absVal）", viewModel.VisibleFields[0].Name);
        Assert.Equal("-5", viewModel.VisibleFields[0].Value);
        Assert.Equal("内部字段 BigEye · 启用状态（use）", viewModel.VisibleFields[1].Name);
        Assert.Equal("开启", viewModel.VisibleFields[1].Value);
        var item = Assert.Single(viewModel.EffectiveItems);
        Assert.Equal("内部参数 BigEye", item.Name);
        Assert.Equal("大眼", item.DisplayName);
        Assert.True(item.HasOfficialDisplayName);
        Assert.Contains("官方中文名称", viewModel.NameSourceSummary, StringComparison.Ordinal);
        Assert.Equal("界面值 -5", item.CurrentValue);
        Assert.False(item.HasState);
        Assert.Equal(string.Empty, item.State);
    }

    [Fact]
    public void NativeDocumentCombinesRelatedValuesAndSupportsSearch()
    {
        var document = new NativeConfigurationDocument(
            "effect-config",
            "JsonFile",
            "1",
            "WBStore",
            @"WBStore\effectConfigStore.json",
            new string('a', 64),
            Guid.NewGuid(),
            [
                new NativeConfigurationValue(
                    "/effect/items/6938367737173381646/sliders/BigEye/absVal",
                    "Discovery",
                    JsonSerializer.SerializeToElement(-5)),
                new NativeConfigurationValue(
                    "/effect/items/6938367737173381646/sliders/BigEye/use",
                    "Discovery",
                    JsonSerializer.SerializeToElement(true)),
                new NativeConfigurationValue(
                    "/effect/items/6938367737173381646/sliders/BigEye/value",
                    "Discovery",
                    JsonSerializer.SerializeToElement(-0.05)),
                new NativeConfigurationValue(
                    "/effect/items/6938367827954897439/sliders/Clear_ALL/absVal",
                    "Discovery",
                    JsonSerializer.SerializeToElement(20)),
                new NativeConfigurationValue(
                    "/effect/items/6938367827954897439/sliders/Clear_ALL/use",
                    "Discovery",
                    JsonSerializer.SerializeToElement(true))
            ]);

        var viewModel = new SnapshotNativeDocumentViewModel(document);

        Assert.Equal(2, viewModel.EffectiveItems.Count);
        Assert.Equal(2, viewModel.VisibleItems.Count);
        Assert.Equal("大眼", viewModel.VisibleItems[0].DisplayName);
        Assert.Equal("清晰", viewModel.VisibleItems[1].DisplayName);
        Assert.Contains("原生值 -0.05", viewModel.VisibleItems[0].Detail, StringComparison.Ordinal);

        viewModel.ItemSearchText = "清晰";

        var result = Assert.Single(viewModel.VisibleItems);
        Assert.Equal("内部参数 Clear_ALL", result.Name);
        Assert.Contains("共 1 项", viewModel.ItemPageSummary, StringComparison.Ordinal);

        viewModel.ItemSearchText = "missing-value";

        Assert.Empty(viewModel.VisibleItems);
        Assert.False(viewModel.HasVisibleItems);
        Assert.Equal("没有匹配的有效配置", viewModel.ItemPageSummary);
    }

    [Fact]
    public void EffectDocumentShowsCurrentValuesByDefaultAndKeepsPresetsInASeparateChineseView()
    {
        const string config = "/effectConfigStore/configs/current";
        const string resource = "6938367737173381646";
        const string preset = "preset-one";
        var document = new NativeConfigurationDocument(
            "effect-config", "JsonFile", "1", "WBStore", @"WBStore\effectConfigStore.json",
            new string('a', 64), Guid.NewGuid(),
            [
                new NativeConfigurationValue($"{config}/effect/items/{resource}/sliders/BigEye/absVal", "Discovery",
                    JsonSerializer.SerializeToElement(12)),
                new NativeConfigurationValue($"{config}/effect/items/{resource}/sliders/BigEye/use", "Discovery",
                    JsonSerializer.SerializeToElement(true)),
                new NativeConfigurationValue($"{config}/effect/temps/{preset}/name", "Discovery",
                    JsonSerializer.SerializeToElement("预设1")),
                new NativeConfigurationValue($"{config}/effect/temps/{preset}/items/{resource}/sliders/BigEye/absVal", "Discovery",
                    JsonSerializer.SerializeToElement(28)),
                new NativeConfigurationValue($"{config}/effect/temps/{preset}/items/{resource}/sliders/BigEye/use", "Discovery",
                    JsonSerializer.SerializeToElement(true))
            ]);

        var viewModel = new SnapshotNativeDocumentViewModel(document);

        var current = Assert.Single(viewModel.EffectiveItems);
        Assert.Equal("大眼", current.DisplayName);
        Assert.False(current.IsPreset);
        Assert.Equal("界面值 12", current.CurrentValue);
        Assert.Single(viewModel.PresetItems);
        Assert.Equal(1, viewModel.PresetGroupCount);
        Assert.Contains("1 个已修改或生效参数", viewModel.UsageSummary, StringComparison.Ordinal);
        Assert.Contains("1 组预设", viewModel.UsageSummary, StringComparison.Ordinal);
        Assert.Equal("共 1 项当前参数 · 连续滚动查看", viewModel.ItemPageSummary);

        viewModel.ShowPresetItems = true;

        var presetItem = Assert.Single(viewModel.VisibleItems);
        Assert.True(presetItem.IsPreset);
        Assert.Equal("美颜预设 · 预设1 · 大眼", presetItem.DisplayName);
        Assert.Equal("界面值 28", presetItem.CurrentValue);
        Assert.Equal("共 1 项预设参数 · 连续滚动查看", viewModel.ItemPageSummary);
    }

    [Fact]
    public void FilterDocumentUsesBusinessNamesAndHidesLocalDirectoryOnDefaultPage()
    {
        var document = new NativeConfigurationDocument(
            "filter-store", "JsonFile", "1", "WBStore", @"WBStore\filterStore.json",
            new string('a', 64), Guid.NewGuid(),
            [
                new NativeConfigurationValue("/filterStore/resourceFilePath", "Discovery",
                    JsonSerializer.SerializeToElement(@"C:\Users\tester\filters")),
                new NativeConfigurationValue("/filterStore/useNewFilter", "Discovery",
                    JsonSerializer.SerializeToElement(true)),
                new NativeConfigurationValue("/filterStore/filterTempList/0/name", "Discovery",
                    JsonSerializer.SerializeToElement("默认滤镜"))
            ]);

        var viewModel = new SnapshotNativeDocumentViewModel(document);

        Assert.Contains(viewModel.EffectiveItems, item =>
            item.DisplayName == "自定义滤镜素材目录" && item.CurrentValue == "已设置目录");
        Assert.Contains(viewModel.EffectiveItems, item => item.DisplayName == "新版滤镜引擎");
        Assert.Contains(viewModel.EffectiveItems, item => item.DisplayName == "默认滤镜");
        Assert.DoesNotContain(viewModel.EffectiveItems, item => item.CurrentValue.Contains(@"C:\Users", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeFieldRedactsSensitiveJsonStoredInsideString()
    {
        var field = new SnapshotNativeFieldViewModel(new NativeConfigurationValue(
            "/effectStore/giftPlayConfig/speechConfig",
            "Discovery",
            JsonSerializer.SerializeToElement("{\"appToken\":\"must-not-render\"}")));

        Assert.Equal("已隐藏敏感内容", field.Value);
        Assert.DoesNotContain("must-not-render", field.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectStateUsesCompactChineseSummariesInsteadOfRawIdsAndLinks()
    {
        var document = new NativeConfigurationDocument(
            "effect-store", "JsonFile", "1", "WBStore", @"WBStore\effectStore.json",
            new string('a', 64), Guid.NewGuid(),
            [
                new NativeConfigurationValue("/effectStore/giftPlayConfig/groupEffectIds/0", "Discovery",
                    JsonSerializer.SerializeToElement(10001)),
                new NativeConfigurationValue("/effectStore/giftPlayConfig/groupEffectIds/1", "Discovery",
                    JsonSerializer.SerializeToElement(10002)),
                new NativeConfigurationValue("/effectStore/giftPlayConfig/serviceSchema", "Discovery",
                    JsonSerializer.SerializeToElement("https://example.invalid/very-long-internal-route")),
                new NativeConfigurationValue("/effectStore/giftPlayConfig/queueLength", "Discovery",
                    JsonSerializer.SerializeToElement(20)),
                new NativeConfigurationValue("/effectStore/giftPlayConfig/sourceTransparency", "Discovery",
                    JsonSerializer.SerializeToElement(0.75)),
                new NativeConfigurationValue("/effectStore/shownTips/camera-mattingSetting", "Discovery",
                    JsonSerializer.SerializeToElement(true)),
                new NativeConfigurationValue("/effectStore/shownTips/useEffectUiV3-panelMerge", "Discovery",
                    JsonSerializer.SerializeToElement(false)),
                new NativeConfigurationValue("/effectStore/deviceFacing/camera-source-id", "Discovery",
                    JsonSerializer.SerializeToElement(1787580000000L)),
                new NativeConfigurationValue("/effectStore/entityStamp", "Discovery",
                    JsonSerializer.SerializeToElement("internal-version-stamp"))
            ]);

        var items = new SnapshotNativeDocumentViewModel(document).EffectiveItems;

        Assert.Contains(items, item => item.DisplayName == "礼物特效分组"
                                      && item.CurrentValue == "已保存 2 个特效编号");
        Assert.Contains(items, item => item.DisplayName == "礼物特效播放配置"
                                      && item.CurrentValue.Contains("项有内容", StringComparison.Ordinal)
                                      && item.Detail.Contains("播放队列长度 20", StringComparison.Ordinal));
        Assert.Contains(items, item => item.DisplayName == "功能提示记录"
                                      && item.CurrentValue == "当前 1 项提示已显示");
        Assert.Contains(items, item => item.DisplayName == "摄像头方向记录"
                                      && item.CurrentValue == "已记录");
        Assert.DoesNotContain(items, item => item.DisplayName is "其他配置项" or "其他参数");
        Assert.DoesNotContain(items, item => item.CurrentValue.Contains("https://", StringComparison.Ordinal)
                                            || item.Detail.Contains("https://", StringComparison.Ordinal)
                                            || item.CurrentValue.Contains("178758", StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.TechnicalPath.EndsWith("/entityStamp", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceDocumentAggregatesCurveAnchorsWithChineseLabels()
    {
        var document = new NativeConfigurationDocument(
            "source-store", "JsonFile", "1", "WBStore", @"WBStore\sourceStore.json",
            new string('a', 64), Guid.NewGuid(),
            [
                new NativeConfigurationValue(
                    "/sourceStore/data/camera/payload/filterData/filterDataList/5/payload/curves/0/lum/anchors/0/position/x",
                    "Discovery", JsonSerializer.SerializeToElement(0.25)),
                new NativeConfigurationValue(
                    "/sourceStore/data/camera/payload/filterData/filterDataList/5/payload/curves/0/lum/anchors/0/position/y",
                    "Discovery", JsonSerializer.SerializeToElement(0.5))
            ]);

        var item = Assert.Single(new SnapshotNativeDocumentViewModel(document).EffectiveItems);
        Assert.Equal("滤镜第 6 项 · 曲线通道 1 · 控制点 1", item.DisplayName);
        Assert.Contains("横坐标", item.CurrentValue, StringComparison.Ordinal);
        Assert.Contains("纵坐标", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceDocumentShowsSourceNameAndCompleteVideoModeSummary()
    {
        var prefix = "/sourceStore/sceneSource/scene4/data/36f28b20-d31c-43ff-b0c7-9f88810dae65";
        var document = new NativeConfigurationDocument(
            "source-store", "JsonFile", "1", "WBStore", @"WBStore\sourceStore.json",
            new string('a', 64), Guid.NewGuid(),
            [
                new NativeConfigurationValue($"{prefix}/name", "Discovery", JsonSerializer.SerializeToElement("OBS Virtual Camera")),
                new NativeConfigurationValue($"{prefix}/type", "Discovery", JsonSerializer.SerializeToElement("camera")),
                new NativeConfigurationValue($"{prefix}/payload/width", "Discovery", JsonSerializer.SerializeToElement(1920)),
                new NativeConfigurationValue($"{prefix}/payload/height", "Discovery", JsonSerializer.SerializeToElement(1080)),
                new NativeConfigurationValue($"{prefix}/payload/rate", "Discovery", JsonSerializer.SerializeToElement(30)),
                new NativeConfigurationValue($"{prefix}/payload/format", "Discovery", JsonSerializer.SerializeToElement(6)),
                new NativeConfigurationValue($"{prefix}/payload/colorSpace", "Discovery", JsonSerializer.SerializeToElement(2)),
                new NativeConfigurationValue($"{prefix}/payload/videoRange", "Discovery", JsonSerializer.SerializeToElement(2))
            ]);

        var items = new SnapshotNativeDocumentViewModel(document).EffectiveItems;
        Assert.Contains(items, item => item.DisplayName == "视频来源 · OBS Virtual Camera");
        Assert.Contains(items, item => item.DisplayName == "视频参数"
                                      && item.CurrentValue == "1920 × 1080 · 30 FPS"
                                      && item.Detail.Contains("色彩空间 2", StringComparison.Ordinal)
                                      && item.Detail.Contains("色彩范围 2", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeDocumentHighlightsOnlyValuesWithContent()
    {
        var document = new NativeConfigurationDocument(
            "effect-config",
            "JsonFile",
            "1",
            "WBStore",
            "effectStore.json",
            new string('a', 64),
            Guid.NewGuid(),
            [
                new NativeConfigurationValue("/disabled", "Discovery", JsonSerializer.SerializeToElement(false)),
                new NativeConfigurationValue("/zero", "Discovery", JsonSerializer.SerializeToElement(0)),
                new NativeConfigurationValue("/empty", "Discovery", JsonSerializer.SerializeToElement(string.Empty)),
                new NativeConfigurationValue("/configId", "Discovery", JsonSerializer.SerializeToElement(Guid.NewGuid())),
                new NativeConfigurationValue("/enabled", "Discovery", JsonSerializer.SerializeToElement(true)),
                new NativeConfigurationValue("/strength", "Discovery", JsonSerializer.SerializeToElement(15))
            ]);

        var viewModel = new SnapshotNativeDocumentViewModel(document);

        Assert.Equal(2, viewModel.MeaningfulFieldCount);
        Assert.Equal(["enabled", "strength"], viewModel.HighlightedFields.Select(field => field.Name));
        Assert.Contains("2 项有内容", viewModel.HighlightSummary, StringComparison.Ordinal);
        Assert.Equal(6, viewModel.Fields.Count);
    }

    [Fact]
    public void ObsSourceSeparatesNonDefaultSettingsFromCompleteInputSettings()
    {
        var source = new VideoSource(
            Guid.NewGuid(),
            "采集卡",
            "dshow_input",
            null,
            null,
            new Dictionary<string, JsonElement>
            {
                ["unchanged"] = JsonSerializer.SerializeToElement(1),
                ["enabled"] = JsonSerializer.SerializeToElement(true),
                ["monitor_id"] = JsonSerializer.SerializeToElement("internal-display-identifier")
            },
            [],
            DefaultSettings: new Dictionary<string, JsonElement>
            {
                ["unchanged"] = JsonSerializer.SerializeToElement(1),
                ["enabled"] = JsonSerializer.SerializeToElement(false)
            });

        var viewModel = new SnapshotSourceViewModel(source);

        Assert.Equal(3, viewModel.AllSettings.Count);
        Assert.Empty(viewModel.Settings);
        Assert.Contains(viewModel.AllSettings, setting => setting.TechnicalName == "enabled"
                                                        && setting.Value == "开启");
    }

    [Fact]
    public void ObsDefaultDisplayUsesChineseNamesAndKeepsRawKeysForTechnicalDetails()
    {
        var filter = new VideoFilter(
            Guid.NewGuid(),
            "色彩校正",
            "color_filter_v2",
            true,
            0,
            new Dictionary<string, JsonElement>
            {
                ["brightness"] = JsonSerializer.SerializeToElement(0.15)
            },
            []);
        var source = new VideoSource(
            Guid.NewGuid(),
            "显示器采集",
            "monitor_capture",
            null,
            null,
            new Dictionary<string, JsonElement>(),
            [filter]);

        var sourceViewModel = new SnapshotSourceViewModel(source);
        var filterViewModel = Assert.Single(sourceViewModel.Filters);

        Assert.Equal("显示器采集", sourceViewModel.SourceKind);
        Assert.Equal("monitor_capture", sourceViewModel.TechnicalSourceKind);
        Assert.Equal("色彩校正", filterViewModel.Kind);
        Assert.Equal("color_filter_v2", filterViewModel.TechnicalKind);
        var setting = Assert.Single(filterViewModel.Settings);
        Assert.Equal("亮度", setting.Name);
        Assert.Equal("brightness", setting.TechnicalName);
        Assert.Equal("亮度 0.15", setting.Summary);
    }

    [Theory]
    [InlineData("luma_max_smooth", "0.1", "亮度上限平滑", "0.1")]
    [InlineData("key_color", "65280", "键颜色", "#00FF00")]
    [InlineData("color_multiply", "16777215", "乘算颜色", "#FFFFFF")]
    [InlineData("key_color_type", "\"green\"", "键颜色类型", "绿色")]
    public void ObsFilterDefaultsUseChineseLabelsAndReadableValues(
        string key,
        string json,
        string expectedName,
        string expectedValue)
    {
        var value = JsonDocument.Parse(json).RootElement.Clone();
        var filter = new VideoFilter(
            Guid.NewGuid(), "测试滤镜", "color_filter_v2", true, 0,
            new Dictionary<string, JsonElement> { [key] = value }, []);

        var setting = Assert.Single(new SnapshotFilterViewModel(filter).Settings);
        Assert.Equal(expectedName, setting.Name);
        Assert.Equal(expectedValue, setting.Value);
    }

    [Theory]
    [InlineData("/payload/colorSpace", "2", "709")]
    [InlineData("/payload/videoRange", "2", "全部")]
    [InlineData("/payload/format", "6", "YUY2")]
    [InlineData("/filterData/filterDataList/0/type", "28", "HSL")]
    [InlineData("/filterData/filterDataList/0/selectedHueKey", "red", "红色")]
    [InlineData("/filterData/filterDataList/0/colorMultiply", "16777215", "#FFFFFF")]
    [InlineData("/giftPlayConfig/preDuration", "7.2", "7.2 秒")]
    [InlineData("/giftPlayConfig/sourceTransparency", "0.4", "40%")]
    public void LiveCompanionOfficialEnumsUseNativeChineseDisplay(
        string nativePath,
        string value,
        string expected)
    {
        var field = new CapturedParameterField(
            $"sourceStore.json:{nativePath}",
            "Discovery",
            "Number",
            true,
            false,
            "DiscoveryReadOnly",
            NativeName: "原生设置",
            UiPath: "滤镜设置/视频滤镜链/原生设置",
            EvidenceStatus: FieldEvidenceStatus.EvidenceOnly);

        var viewModel = new SnapshotCoverageFieldViewModel(field, value, false);

        Assert.Equal(expected, viewModel.DisplayValue);
    }

    [Fact]
    public void InternalRuntimeCacheKeysStayOutOfTheDefaultMenuView()
    {
        var field = new CapturedParameterField(
            "effectStore.json:/effectStore/deviceFacing/1787639397245",
            "Discovery",
            "Number",
            true,
            false,
            "DiscoveryReadOnly",
            NativeName: "内部参数",
            UiPath: "基础设置/镜头方向/内部参数",
            EvidenceStatus: FieldEvidenceStatus.EvidenceOnly);

        var viewModel = new SnapshotCoverageFieldViewModel(field, "1787639397245", false);

        var activityField = field with
        {
            NativePath = "effectStore.json:/effectStore/carnivalInfo/0",
            NativeName = "第 1 项",
            UiPath = "特效道具/活动特效/第 1 项"
        };
        var activityViewModel = new SnapshotCoverageFieldViewModel(activityField, "207981615", false);

        Assert.False(viewModel.ShowByDefault);
        Assert.False(activityViewModel.ShowByDefault);
    }

    [Fact]
    public void LiveCompanionMenuHidesFilterMetadataAndCompactsChangedHuePoint()
    {
        var prefix = "sourceStore.json:/sourceStore/filterData/filterDataList/0";
        var uiPrefix = "滤镜设置/视频滤镜链/第 1 个滤镜 · HSL";
        var fields = new[]
        {
            CoverageField($"{prefix}/name", $"{uiPrefix}/名称", "HSL"),
            CoverageField($"{prefix}/type", $"{uiPrefix}/类型", "28"),
            CoverageField($"{prefix}/payload/points/0/hue", $"{uiPrefix}/色相", "0.083333"),
            CoverageField($"{prefix}/payload/points/0/hueAdjust", $"{uiPrefix}/色相调整", "0.04"),
            CoverageField($"{prefix}/payload/points/0/saturationAdjust", $"{uiPrefix}/饱和度调整", "0.03"),
            CoverageField($"{prefix}/payload/points/0/lightnessAdjust", $"{uiPrefix}/明度调整", "0"),
            CoverageField($"{prefix}/payload/points/1/hue", $"{uiPrefix}/色相", "0.166667"),
            CoverageField($"{prefix}/payload/points/1/hueAdjust", $"{uiPrefix}/色相调整", "0"),
            CoverageField($"{prefix}/payload/points/1/saturationAdjust", $"{uiPrefix}/饱和度调整", "0"),
            CoverageField($"{prefix}/payload/points/1/lightnessAdjust", $"{uiPrefix}/明度调整", "0")
        };

        var row = Assert.Single(SnapshotCoverageFieldViewModel.CreateCompactMenuRows(fields));

        Assert.Equal("视频滤镜链 · 第 1 个滤镜 · HSL · 红色", row.MenuLabel);
        Assert.Equal("红色", row.CompactMenuLabel);
        Assert.Equal("色相 0.04 · 饱和度 0.03", row.DisplayValue);
        Assert.Equal("视频滤镜链 · 第 1 个滤镜 · HSL · 红色：色相 0.04 · 饱和度 0.03", row.FullDisplayText);
    }

    [Fact]
    public void LiveCompanionMenuDoesNotListUntouchedDefaultCurve()
    {
        var prefix = "sourceStore.json:/sourceStore/filterData/filterDataList/5/payload/curves/4";
        var uiPrefix = "滤镜设置/视频滤镜链/第 6 个滤镜 · 曲线/曲线/明度对饱和度";
        var fields = new[]
        {
            CoverageField($"{prefix}/enable", $"{uiPrefix}/启用", "true"),
            CoverageField($"{prefix}/type", $"{uiPrefix}/类型", "4"),
            CoverageField($"{prefix}/main/anchors/0/position/x", $"{uiPrefix}/控制点 1/位置/横坐标", "0"),
            CoverageField($"{prefix}/main/anchors/0/position/y", $"{uiPrefix}/控制点 1/位置/纵坐标", "0"),
            CoverageField($"{prefix}/main/anchors/0/rightControl/x", $"{uiPrefix}/控制点 1/右控制柄/横坐标", "0.5"),
            CoverageField($"{prefix}/main/anchors/0/rightControl/y", $"{uiPrefix}/控制点 1/右控制柄/纵坐标", "0"),
            CoverageField($"{prefix}/main/anchors/1/leftControl/x", $"{uiPrefix}/控制点 2/左控制柄/横坐标", "0.5"),
            CoverageField($"{prefix}/main/anchors/1/leftControl/y", $"{uiPrefix}/控制点 2/左控制柄/纵坐标", "0"),
            CoverageField($"{prefix}/main/anchors/1/position/x", $"{uiPrefix}/控制点 2/位置/横坐标", "1"),
            CoverageField($"{prefix}/main/anchors/1/position/y", $"{uiPrefix}/控制点 2/位置/纵坐标", "0")
        };

        Assert.Empty(SnapshotCoverageFieldViewModel.CreateCompactMenuRows(fields));
    }

    [Fact]
    public void LiveCompanionMenuCompactsChangedCurveWithNativeCoordinateLabels()
    {
        var prefix = "sourceStore.json:/sourceStore/filterData/filterDataList/5/payload/curves/0";
        var uiPrefix = "滤镜设置/视频滤镜链/第 6 个滤镜 · 曲线/曲线/Master/控制点 1";
        var fields = new[]
        {
            CoverageField($"{prefix}/lum/anchors/0/position/x", $"{uiPrefix}/位置 · 横坐标", "0"),
            CoverageField($"{prefix}/lum/anchors/0/position/y", $"{uiPrefix}/位置 · 纵坐标", "0.125"),
            CoverageField($"{prefix}/lum/anchors/0/rightControl/x", $"{uiPrefix}/右控制柄 · 横坐标", "0.333333333"),
            CoverageField($"{prefix}/lum/anchors/0/rightControl/y", $"{uiPrefix}/右控制柄 · 纵坐标", "0.25")
        };

        var row = Assert.Single(SnapshotCoverageFieldViewModel.CreateCompactMenuRows(fields));

        Assert.Equal("视频滤镜链 · 第 6 个滤镜 · 曲线 · Master · 控制点 1", row.MenuLabel);
        Assert.Equal("位置 0 / 0.125 · 右控制柄 0.333 / 0.25", row.DisplayValue);
    }

    [Fact]
    public void LiveCompanionMenuHidesActivationRowsAndDisabledSubtrees()
    {
        var enabledPrefix = "sourceStore.json:/filterData/filterDataList/0";
        var disabledPrefix = "sourceStore.json:/filterData/filterDataList/1";
        var fields = new[]
        {
            CoverageField($"{enabledPrefix}/enable", "滤镜设置/视频滤镜链/第 1 个滤镜 · HSL/启用", "true"),
            CoverageField($"{enabledPrefix}/payload/strength", "滤镜设置/视频滤镜链/第 1 个滤镜 · HSL/强度", "24"),
            CoverageField($"{disabledPrefix}/use", "滤镜设置/视频滤镜链/第 2 个滤镜 · LUT/启用", "false"),
            CoverageField($"{disabledPrefix}/payload/strength", "滤镜设置/视频滤镜链/第 2 个滤镜 · LUT/强度", "80")
        };

        var row = Assert.Single(SnapshotCoverageFieldViewModel.CreateCompactMenuRows(fields));

        Assert.Equal("24", row.DisplayValue);
        Assert.EndsWith("强度", row.MenuLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("开启", row.DisplayValue, StringComparison.Ordinal);
        Assert.DoesNotContain("LUT", row.MenuLabel, StringComparison.Ordinal);
        Assert.Equal(4, fields.Length);
        Assert.Contains(fields, field => field.TechnicalPath.EndsWith("/use", StringComparison.Ordinal));
    }

    [Fact]
    public void ObsPresentationUsesSemanticBooleanAsLabelAndHidesFalseValue()
    {
        var settings = new Dictionary<string, JsonElement>
        {
            ["horizontal_flip"] = JsonSerializer.SerializeToElement(true),
            ["vertical_flip"] = JsonSerializer.SerializeToElement(false)
        };

        var source = new VideoSource(Guid.NewGuid(), "采集卡", "dshow_input", null, null, settings, []);
        var item = Assert.Single(new SnapshotSourceViewModel(source).Settings);

        Assert.Contains("翻转", item.Name, StringComparison.Ordinal);
        Assert.False(item.HasValue);
        Assert.DoesNotContain("开启", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ObsPresentationHidesDisabledObjectButKeepsTechnicalSettings()
    {
        var source = new VideoSource(
            Guid.NewGuid(),
            "采集卡",
            "dshow_input",
            null,
            null,
            new Dictionary<string, JsonElement>
            {
                ["crop"] = JsonSerializer.SerializeToElement(new { use = false, left = 18 })
            },
            []);

        var viewModel = new SnapshotSourceViewModel(source);

        Assert.Empty(viewModel.Settings);
        Assert.Contains(viewModel.AllSettings, setting => setting.TechnicalName == "crop/use");
        Assert.Contains(viewModel.AllSettings, setting => setting.TechnicalName == "crop/left");
    }

    [Fact]
    public void ObsDefaultPageExcludesDisabledFiltersWithoutDroppingArchiveData()
    {
        var enabled = new VideoFilter(Guid.NewGuid(), "色彩校正", "color_filter_v2", true, 0,
            new Dictionary<string, JsonElement>(), []);
        var disabled = new VideoFilter(Guid.NewGuid(), "旧 LUT", "clut_filter", false, 1,
            new Dictionary<string, JsonElement>(), []);
        var source = new VideoSource(Guid.NewGuid(), "采集卡", "dshow_input", null, null,
            new Dictionary<string, JsonElement>(), [enabled, disabled]);

        var viewModel = new SnapshotSourceViewModel(source);

        var filter = Assert.Single(viewModel.Filters);
        Assert.Equal("色彩校正", filter.Name);
        Assert.Equal(2, source.Filters.Count);
    }

    [Fact]
    public void LiveCompanionPresentationKeepsMeaningfulCurveZero()
    {
        var field = CoverageField(
            "sourceStore.json:/filterData/filterDataList/5/payload/curves/0/lum/anchors/0/position/x",
            "滤镜设置/视频滤镜链/第 6 个滤镜 · 曲线/Master/控制点 1/横坐标",
            "0");

        Assert.True(field.ShowByDefault);
        Assert.Equal("0", field.DisplayValue);
    }

    private static SnapshotCoverageFieldViewModel CoverageField(
        string nativePath,
        string uiPath,
        string value) => new(
        new CapturedParameterField(
            nativePath,
            "Discovery",
            "Number",
            true,
            false,
            "DiscoveryReadOnly",
            NativeName: uiPath.Split('/').Last(),
            UiPath: uiPath,
            EvidenceStatus: FieldEvidenceStatus.EvidenceOnly),
        value,
        false);

    private static ApplicationSnapshot CreateApplication(ApplicationKind kind) => new(
        kind,
        kind == ApplicationKind.Obs ? "32.2.2" : "12.8.1.454484231",
        kind == ApplicationKind.Obs ? "obs-websocket-5" : "live-companion-test",
        string.Empty,
        new string('a', 64),
        CompatibilityLevel.Unsupported,
        true,
        [],
        [],
        [],
        CreateTree());

    private static ConfigurationTreeSnapshot CreateTree()
    {
        var names = new[]
        {
            "基础设置", "美颜设置", "美妆设置", "滤镜设置", "特效道具", "绿幕抠图", "镜头特效", "导入导出"
        };
        var sections = names.Select((name, index) => new ConfigurationSectionSnapshot(
                name,
                name,
                name,
                index,
                [],
                []))
            .Append(new ConfigurationSectionSnapshot(
                "native-unmapped",
                "未映射原生字段",
                "未映射原生字段",
                names.Length,
                [],
                []))
            .ToArray();
        return new ConfigurationTreeSnapshot(sections, 0, 2, 0, 0, false, false);
    }
}
