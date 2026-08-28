using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class MainViewModelStartupTests
{
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
