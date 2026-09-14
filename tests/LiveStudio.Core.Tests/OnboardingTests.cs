using LiveStudio.Desktop.Services;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class OnboardingTests
{
    [Fact]
    public void SkipIsRememberedAndManualReplayStillWorks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "livestudio-guide-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TutorialPreferenceStore(Path.Combine(directory, "guide.txt"));
            var guide = new OnboardingViewModel(store);
            guide.ShowIfFirstUse();
            Assert.True(guide.IsOpen);
            guide.NextCommand.Execute(null);
            guide.DismissCommand.Execute(null);
            Assert.False(guide.IsOpen);
            var nextRun = new OnboardingViewModel(store);
            nextRun.ShowIfFirstUse();
            Assert.False(nextRun.IsOpen);
            nextRun.OpenCommand.Execute(null);
            Assert.True(nextRun.IsOpen);
            Assert.Equal(0, nextRun.StepIndex);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void IndexNavigationAndCompletionStayWithinSteps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "livestudio-guide-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TutorialPreferenceStore(Path.Combine(directory, "guide.txt"));
            var guide = new OnboardingViewModel(store);
            guide.OpenCommand.Execute(null);
            Assert.False(guide.PreviousCommand.CanExecute(null));
            guide.SelectStepCommand.Execute("999");
            Assert.Equal(0, guide.StepIndex);
            guide.SelectStepCommand.Execute("4");
            Assert.Equal("开始使用", guide.NextLabel);
            Assert.True(guide.PreviousCommand.CanExecute(null));
            guide.NextCommand.Execute(null);
            Assert.False(guide.IsOpen);
            Assert.True(store.HasSeen());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
