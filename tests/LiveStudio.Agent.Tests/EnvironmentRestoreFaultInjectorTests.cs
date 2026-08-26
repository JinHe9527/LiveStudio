using LiveStudio.Core;

namespace LiveStudio.Agent.Tests;

public sealed class EnvironmentRestoreFaultInjectorTests
{
    [Fact]
    public async Task MissingFaultPointKeepsInjectionDisabled()
    {
        var injector = new EnvironmentRestoreFaultInjector(null, null);

        await injector.InjectAsync(RestoreFaultPoint.SettingsApplied, CancellationToken.None);
    }

    [Fact]
    public void FaultPointWithoutDestructiveConfirmationIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new EnvironmentRestoreFaultInjector(null, nameof(RestoreFaultPoint.SettingsApplied)));

        Assert.Contains(
            EnvironmentRestoreFaultInjector.ConfirmationVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedFaultPointThrowsOnlyAtSelectedBoundary()
    {
        var injector = new EnvironmentRestoreFaultInjector(
            EnvironmentRestoreFaultInjector.RequiredConfirmation,
            nameof(RestoreFaultPoint.SettingsApplied));

        await injector.InjectAsync(RestoreFaultPoint.AssetsPrepared, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            injector.InjectAsync(RestoreFaultPoint.SettingsApplied, CancellationToken.None));
        Assert.Contains(nameof(RestoreFaultPoint.SettingsApplied), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashBehaviorWithoutSecondConfirmationIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new EnvironmentRestoreFaultInjector(
                EnvironmentRestoreFaultInjector.RequiredConfirmation,
                nameof(RestoreFaultPoint.SettingsApplied),
                "Crash"));

        Assert.Contains(
            EnvironmentRestoreFaultInjector.CrashConfirmationVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedCrashBehaviorDoesNotTerminateAtAnotherBoundary()
    {
        var injector = new EnvironmentRestoreFaultInjector(
            EnvironmentRestoreFaultInjector.RequiredConfirmation,
            nameof(RestoreFaultPoint.SettingsApplied),
            "Crash",
            EnvironmentRestoreFaultInjector.RequiredCrashConfirmation);

        await injector.InjectAsync(RestoreFaultPoint.AssetsPrepared, CancellationToken.None);
    }
}
