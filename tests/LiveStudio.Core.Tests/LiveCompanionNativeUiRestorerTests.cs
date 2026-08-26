using LiveStudio.Adapters.LiveCompanion;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionNativeUiRestorerTests
{
    [Theory]
    [InlineData("摄像头设置", 640, 660)]
    [InlineData("摄像头设置 - OBS Virtual Camera", 900, 900)]
    public void CameraSettingsTitleTakesPriorityOverWindowScale(
        string title,
        int width,
        int height)
    {
        Assert.True(LiveCompanionNativeUiRestorer.MatchesCameraSettingsWindow(
            "Chrome_WidgetWin_1",
            title,
            width,
            height));
    }

    [Fact]
    public void UnrelatedTitledWindowIsNotTreatedAsCameraSettings()
    {
        Assert.False(LiveCompanionNativeUiRestorer.MatchesCameraSettingsWindow(
            "Chrome_WidgetWin_1",
            "直播伴侣",
            640,
            660));
    }

    [Theory]
    [InlineData("添加摄像头", 900, 700)]
    [InlineData("选择摄像头设备", 640, 660)]
    public void AddCameraTitleTakesPriorityOverWindowScale(
        string title,
        int width,
        int height)
    {
        Assert.True(LiveCompanionNativeUiRestorer.MatchesAddCameraWindow(
            "Chrome_WidgetWin_1",
            title,
            width,
            height));
    }

    [Fact]
    public void UnrelatedTitledWindowIsNotTreatedAsAddCamera()
    {
        Assert.False(LiveCompanionNativeUiRestorer.MatchesAddCameraWindow(
            "Chrome_WidgetWin_1",
            "直播伴侣",
            900,
            520));
    }
}
