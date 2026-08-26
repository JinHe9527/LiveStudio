using LiveStudio.Adapters.LiveCompanion;

namespace LiveStudio.Core.Tests;

public sealed class WindowsAutomationBridgeTests
{
    [Fact]
    public void ValidateRuntimeLoadsWindowsAutomationTypesAndPatterns()
    {
        WindowsAutomationBridge.ValidateRuntime();
    }
}
