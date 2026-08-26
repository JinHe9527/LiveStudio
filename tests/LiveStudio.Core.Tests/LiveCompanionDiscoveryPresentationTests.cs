using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionDiscoveryPresentationTests
{
    [Theory]
    [InlineData("effect", "美颜设置/美颜/当前参数")]
    [InlineData("slim", "美颜设置/美体/当前参数")]
    [InlineData("makeupstyle", "美妆设置/风格整妆/当前参数")]
    [InlineData("makeupsingle", "美妆设置/自定义美妆/当前参数")]
    [InlineData("composerfilter", "滤镜设置/风格滤镜/当前参数")]
    [InlineData("default", "特效道具/特效道具/当前参数")]
    [InlineData("matting", "绿幕抠图/背景设置/当前参数")]
    [InlineData("lens-effect", "镜头特效/运镜效果/当前参数")]
    [InlineData("atmosphere", "镜头特效/镜头氛围/当前参数")]
    public void OfficialPanelKeysUseLiveCompanionChineseMenus(string panel, string expectedPrefix)
    {
        var pointer = $"/effectConfigStore/configs/current/{panel}/items/item/use";
        var document = Document("effectConfigStore.json", pointer, true);

        var presentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);

        Assert.StartsWith(expectedPrefix, presentation.UiPath, StringComparison.Ordinal);
        Assert.Equal("启用", presentation.NativeName);
    }

    [Fact]
    public void OfficialBeautySliderUsesChineseNameAndSeparatesPresets()
    {
        const string current = "/effectConfigStore/configs/current/effect/items/6938367737173381646/sliders/BigEye/absVal";
        const string preset = "/effectConfigStore/configs/current/effect/temps/preset/items/6938367737173381646/sliders/BigEye/value";
        var document = Document("effectConfigStore.json", current, 0.12, preset, "28");

        var currentPresentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);
        var presetPresentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[1]);

        Assert.Equal("大眼 · 原生值", currentPresentation.NativeName);
        Assert.StartsWith("美颜设置/美颜/当前参数", currentPresentation.UiPath, StringComparison.Ordinal);
        Assert.Equal("大眼 · 界面值", presetPresentation.NativeName);
        Assert.StartsWith("美颜设置/美颜/预设参数", presetPresentation.UiPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFieldsUseDeviceModeAndChineseCurveChannels()
    {
        const string prefix = "/sourceStore/sceneSource/scene4/data/source/payload";
        var document = Document(
            "sourceStore.json",
            $"{prefix}/deviceId", "device",
            $"{prefix}/filterData/filterDataList/5/payload/curves/0/type", 0,
            $"{prefix}/filterData/filterDataList/5/payload/curves/0/red/anchors/1/position/x", 0.5,
            $"{prefix}/filterData/filterDataList/5/payload/curves/2/type", 2,
            $"{prefix}/filterData/filterDataList/5/payload/curves/2/main/anchors/0/position/y", 0.6);

        var device = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);
        var red = LiveCompanionDiscoveryPresentation.Present(document, document.Values[2]);
        var hueSaturation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[4]);

        Assert.Equal("基础设置/设备与视频模式/视频设备", device.UiPath);
        Assert.Contains("/曲线/R/控制点 2/", red.UiPath, StringComparison.Ordinal);
        Assert.Equal("位置 · 横坐标", red.NativeName);
        Assert.Contains("/曲线/色相对饱和度/控制点 1/", hueSaturation.UiPath, StringComparison.Ordinal);
        Assert.Equal("位置 · 纵坐标", hueSaturation.NativeName);
    }

    [Fact]
    public void AggregateCurveChannelDoesNotCreateFalseMenuLevels()
    {
        const string pointer = "/sourceStore/sceneSource/scene4/data/source/payload/filterData/filterDataList/5/payload/curves/0/type";
        var document = Document("sourceStore.json", pointer, 0);

        var presentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);

        Assert.Contains("/曲线/Master、R、G、B/", presentation.UiPath, StringComparison.Ordinal);
        Assert.DoesNotContain("/Master / R /", presentation.UiPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAndDeviceNamesKeepTheirNativeMeaning()
    {
        const string prefix = "/sourceStore/sceneSource/scene4/data/source";
        var document = Document(
            "sourceStore.json",
            $"{prefix}/name", "直播画面",
            $"{prefix}/payload/name", "采集卡");

        var source = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);
        var device = LiveCompanionDiscoveryPresentation.Present(document, document.Values[1]);

        Assert.Equal("基础设置/视频来源/来源名称", source.UiPath);
        Assert.Equal("基础设置/设备与视频模式/设备名称", device.UiPath);
    }

    [Theory]
    [InlineData("/effectStore/deviceFacing/source", "基础设置/镜头方向")]
    [InlineData("/effectStore/shownTips/camera-mattingSetting", "绿幕抠图/提示状态")]
    [InlineData("/effectStore/sourceUsingLens", "镜头特效/运行状态")]
    [InlineData("/effectStore/giftPlayConfig/queueLength", "特效道具/礼物特效播放")]
    public void EffectRuntimeStateUsesItsOfficialFeatureMenu(string nativePath, string expectedPrefix)
    {
        var document = Document("effectStore.json", nativePath, true);

        var presentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);

        Assert.StartsWith(expectedPrefix, presentation.UiPath, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterStoreUsesFilterSettingsMenu()
    {
        var document = Document("filterStore.json", "/filterStore/filterTempList/0/name", "直播预设");

        var presentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[0]);

        Assert.Equal("滤镜设置/视频滤镜预设/第 1 组/名称", presentation.UiPath);
        Assert.Equal("名称", presentation.NativeName);
    }

    [Fact]
    public void FilterParametersKeepTheNativeFilterNameInTheirCompactGroup()
    {
        const string prefix = "/sourceStore/sceneSource/scene4/data/source/payload/filterData/filterDataList/0";
        var document = Document(
            "sourceStore.json",
            $"{prefix}/name", "HSL",
            $"{prefix}/payload/hueAdjust", 0.04);

        var presentation = LiveCompanionDiscoveryPresentation.Present(document, document.Values[1]);

        Assert.Equal("滤镜设置/视频滤镜链/第 1 个滤镜 · HSL/色相调整", presentation.UiPath);
    }

    private static NativeConfigurationDocument Document(string fileName, params object[] pathValues)
    {
        var values = new List<NativeConfigurationValue>();
        for (var index = 0; index < pathValues.Length; index += 2)
        {
            values.Add(new NativeConfigurationValue(
                (string)pathValues[index],
                "Discovery",
                JsonSerializer.SerializeToElement(pathValues[index + 1])));
        }

        return new NativeConfigurationDocument(
            "webcast_mate",
            "JsonFile",
            "json-v1",
            fileName,
            $@"WBStore\{fileName}",
            new string('a', 64),
            Guid.NewGuid(),
            values);
    }
}
