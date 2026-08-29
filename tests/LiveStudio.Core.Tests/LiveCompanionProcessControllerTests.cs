using LiveStudio.Adapters.LiveCompanion;

namespace LiveStudio.Core.Tests;

public sealed class LiveCompanionProcessControllerTests
{
    [Fact]
    public void LimitedProcessQueryResolvesExactExecutablePathWithoutMainModuleAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var path = LiveCompanionProcessController.TryGetProcessExecutablePath(process.Id);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(
            Path.GetFullPath(Environment.ProcessPath!),
            Path.GetFullPath(path),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesVersionedInstallDirectoryWhenExecutableMetadataDropsBuildNumber()
    {
        var version = LiveCompanionProcessController.ResolveVersion(
            @"D:\webcast_mate\12.8.1.454484231\直播伴侣.exe",
            "12.8.1.0",
            "12.8.1.0");

        Assert.Equal("12.8.1.454484231", version);
    }

    [Fact]
    public void FallsBackToProductVersionOutsideVersionedDirectory()
    {
        var version = LiveCompanionProcessController.ResolveVersion(
            @"C:\Program Files\LiveCompanion\直播伴侣.exe",
            "12.8.1.0",
            "12.8.0.0");

        Assert.Equal("12.8.1.0", version);
    }

    [Theory]
    [InlineData(@"D:\webcast_mate\12.8.1\直播伴侣.exe", @"d:\WEBCAST_MATE\12.8.1\直播伴侣.exe", true)]
    [InlineData(@"D:\webcast_mate\12.8.1\直播伴侣.exe", @"D:\webcast_mate\12.9.0\直播伴侣.exe", false)]
    [InlineData(null, @"D:\webcast_mate\12.8.1\直播伴侣.exe", false)]
    public void ForcedShutdownOnlyTargetsTheExactInstalledExecutable(
        string? candidatePath,
        string expectedPath,
        bool expected)
    {
        Assert.Equal(expected, LiveCompanionProcessController.MatchesExecutablePath(candidatePath, expectedPath));
    }
}
