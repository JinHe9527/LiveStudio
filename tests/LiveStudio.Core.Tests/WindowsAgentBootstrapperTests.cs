using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class WindowsAgentBootstrapperTests
{
    [Fact]
    public void PathsEqualAcceptsCurrentPackagePathCaseInsensitively()
    {
        Assert.True(WindowsAgentBootstrapper.PathsEqual(
            @"C:\Program Files\WindowsApps\LiveStudio_0.1.13\Agent\LiveStudio.Agent.exe",
            @"c:\program files\windowsapps\livestudio_0.1.13\agent\livestudio.agent.exe"));
    }

    [Fact]
    public void PathsEqualRejectsAgentFromPreviousPackageVersion()
    {
        Assert.False(WindowsAgentBootstrapper.PathsEqual(
            @"C:\Program Files\WindowsApps\LiveStudio_0.1.12\Agent\LiveStudio.Agent.exe",
            @"C:\Program Files\WindowsApps\LiveStudio_0.1.13\Agent\LiveStudio.Agent.exe"));
    }
}
