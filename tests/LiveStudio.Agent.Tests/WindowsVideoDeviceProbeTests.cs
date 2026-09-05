using LiveStudio.Agent;

namespace LiveStudio.Agent.Tests;

public sealed class WindowsVideoDeviceProbeTests
{
    [Theory]
    [InlineData(2160, 16, 4096, 2, true)]
    [InlineData(2161, 16, 4096, 2, false)]
    [InlineData(8192, 16, 4096, 2, false)]
    [InlineData(1920, 1920, 1920, 0, true)]
    [InlineData(1920, 16, 4096, 0, false)]
    public void ResolutionRangeRequiresDriverGranularity(int requested, int minimum, int maximum, int granularity, bool supported) =>
        Assert.Equal(supported, WindowsVideoDeviceProbe.MatchesDimension(requested, minimum, maximum, granularity));

    [Theory]
    [InlineData(333333, 166666, 1000000, true)]
    [InlineData(100000, 166666, 1000000, false)]
    [InlineData(2000000, 166666, 1000000, false)]
    [InlineData(333333, 0, 1000000, false)]
    [InlineData(333333, 1000000, 166666, false)]
    [InlineData(0, 1, 1000000, false)]
    public void DriverFrameIntervalMustContainRequestedMode(long requested, long minimum, long maximum, bool supported) =>
        Assert.Equal(supported, WindowsVideoDeviceProbe.MatchesInterval(requested, minimum, maximum));

    [Theory]
    [InlineData("32595559-0000-0010-8000-00aa00389b71", "6", true)]
    [InlineData("32595559-0000-0010-8000-00aa00389b71", "3", false)]
    [InlineData("3231564e-0000-0010-8000-00aa00389b71", "3", true)]
    [InlineData("32595559-0000-0010-8000-00aa00389b71", "999", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", "0", false)]
    public void PixelFormatMustMatchExactNativeSubtype(string subtype, string format, bool supported) =>
        Assert.Equal(supported, WindowsVideoDeviceProbe.MatchesPixelFormat(Guid.Parse(subtype), format));
}
