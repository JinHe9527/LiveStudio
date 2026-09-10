using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class MainViewModelStartupTests
{
    [Fact]
    public void BusyAgentSnapshotDoesNotPermanentlyDisableConnectionButtons()
    {
        var viewModel = new MainViewModel();
        viewModel.ApplyAgentState(CreateState(isBusy: true));

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.AutoConfigureObsCommand.CanExecute(null));
        Assert.True(viewModel.ConfigureObsCommand.CanExecute(null));
        Assert.False(viewModel.CaptureSnapshotCommand.CanExecute(null));
    }

    [Fact]
    public void IdleAgentSnapshotDoesNotUnlockAnActiveDesktopOperation()
    {
        var viewModel = new MainViewModel(true) { IsBusy = true };
        viewModel.ApplyAgentState(CreateState(isBusy: false));

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.AutoConfigureObsCommand.CanExecute(null));
    }

    [Fact]
    public void LoadsThisMachinesPortWithoutOverwritingManualEdits()
    {
        var viewModel = new MainViewModel(true);
        viewModel.ApplyAgentState(CreateState(false) with { ObsEndpoint = "ws://127.0.0.1:4466" });
        Assert.Equal("ws://127.0.0.1:4466", viewModel.ObsEndpoint);

        viewModel.ObsEndpoint = "ws://127.0.0.1:4477";
        viewModel.ObsPassword = "unsaved-test-password";
        viewModel.ApplyAgentState(CreateState(false) with { ObsEndpoint = "ws://127.0.0.1:4466" });
        Assert.Equal("ws://127.0.0.1:4477", viewModel.ObsEndpoint);
        Assert.Equal("unsaved-test-password", viewModel.ObsPassword);
    }

    private static LocalAgentState CreateState(bool isBusy) => new(
        "TEST", false, false, false, isBusy, false, "测试状态", null, "未配置", [], [], []);

    [Fact]
    public void RunningObsWithoutWebSocketShowsConnectionHelp()
    {
        var viewModel = new MainViewModel(true);
        viewModel.ApplyAgentState(CreateState(false) with
        {
            Applications = [new LocalApplicationState(ApplicationKind.Obs, true, true, false, false,
                false, "unknown", "无法读取")]
        });

        Assert.Equal("WebSocket 未连接", viewModel.ObsConnectionState);
        Assert.True(viewModel.ShowObsConnectionHelp);
        Assert.True(viewModel.AutoConfigureObsCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("not-an-endpoint")]
    [InlineData("http://127.0.0.1:4455")]
    [InlineData("ws://192.0.2.1:4455")]
    public async Task InvalidManualEndpointKeepsInputsAndAllowsRetry(string endpoint)
    {
        var viewModel = new MainViewModel { ObsEndpoint = endpoint, ObsPassword = "test-password" };
        await viewModel.ConfigureObsCommand.ExecuteAsync(null);
        Assert.Contains("请输入", viewModel.SettingsMessage, StringComparison.Ordinal);
        Assert.Equal(endpoint, viewModel.ObsEndpoint);
        Assert.Equal("test-password", viewModel.ObsPassword);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.ConfigureObsCommand.CanExecute(null));
    }

    [Fact]
    public async Task WaitForLocalAgentRetriesUntilStateIsAvailable()
    {
        var expected = new LocalAgentState(
            "STUDIO-A",
            false,
            true,
            true,
            false,
            true,
            "已读取 33 份存档",
            null,
            "未配置",
            [],
            [],
            []);
        var attempts = 0;

        var actual = await MainViewModel.WaitForLocalAgentAsync(
            _ => ++attempts < 3
                ? Task.FromException<LocalAgentState>(new IOException("Agent 正在启动"))
                : Task.FromResult(expected),
            maxAttempts: 12,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Same(expected, actual);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForLocalAgentStopsAfterConfiguredAttempts()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            MainViewModel.WaitForLocalAgentAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<LocalAgentState>(new IOException("仍在启动"));
                },
                maxAttempts: 3,
                TimeSpan.Zero,
                CancellationToken.None));

        Assert.Equal(3, attempts);
        Assert.Contains("无法连接 LiveStudio Agent", exception.Message, StringComparison.Ordinal);
    }
}
