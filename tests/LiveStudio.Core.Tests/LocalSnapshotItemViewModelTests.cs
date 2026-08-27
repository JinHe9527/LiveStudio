using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class LocalSnapshotItemViewModelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LocalTimelineOnlyShowsFileSize(bool uploaded, bool uploadEligible)
    {
        var snapshot = new LocalSnapshotSummary(
            Guid.NewGuid(),
            "2026-08-27 20:30",
            DateTimeOffset.UtcNow,
            3 * 1024 * 1024,
            uploaded,
            uploadEligible,
            null);

        var item = new LocalSnapshotItemViewModel(snapshot);

        Assert.Equal("3.0 MB", item.Detail);
        Assert.DoesNotContain("同步", item.Detail, StringComparison.Ordinal);
        Assert.False(item.IsUploadPending);
    }
}
