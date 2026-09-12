using LiveStudio.Contracts;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotInspectorCacheTests
{
    [Fact]
    public void PublishingInspectorRestoresSelectionClearedByTwoWayListBinding()
    {
        var viewModel = new MainViewModel();
        var application = new ApplicationSnapshot(ApplicationKind.Obs, "test", "obs",
            string.Empty, string.Empty, CompatibilityLevel.Experimental, false, [], [], []);
        var inspector = new SnapshotInspectorViewModel("测试", DateTimeOffset.UtcNow, [application]);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SnapshotInspector))
            {
                inspector.SelectedApplication = null;
            }
        };
        viewModel.ShowSnapshotInspector(inspector);
        Assert.Same(inspector.Applications[0], inspector.SelectedApplication);
    }

    [Fact]
    public void RevisitedSnapshotReusesProjectionAndEvictsLeastRecentlyUsed()
    {
        var cache = new SnapshotInspectorCache(2);
        var first = Snapshot();
        var second = Snapshot();
        var third = Snapshot();
        var inspector = Inspector();
        cache.Add(first, inspector);
        cache.Add(second, Inspector());
        Assert.Same(inspector, cache.Get(first));
        cache.Add(third, Inspector());
        Assert.Null(cache.Get(second));
        Assert.Same(inspector, cache.Get(first));
    }

    [Fact]
    public void RefreshedIndexObjectDoesNotReuseOldSnapshotEvenWithSameId()
    {
        var cache = new SnapshotInspectorCache();
        var snapshot = Snapshot();
        cache.Add(snapshot, Inspector());
        Assert.Null(cache.Get(Snapshot(snapshot.Id)));
        cache.Clear();
        Assert.Null(cache.Get(snapshot));
    }

    [Fact]
    public void CameraEditInvalidatesCachedSnapshotInsteadOfReplayingUnsavedValues()
    {
        var cache = new SnapshotInspectorCache();
        var snapshot = Snapshot();
        var inspector = Inspector();
        cache.Add(snapshot, inspector);
        inspector.CameraStations[0].Iso = "800";
        Assert.Null(cache.Get(snapshot));
    }

    [Fact]
    public void EvictedEditorCannotInvalidateReplacementEntry()
    {
        var cache = new SnapshotInspectorCache();
        var snapshot = Snapshot();
        var old = Inspector();
        var current = Inspector();
        cache.Add(snapshot, old);
        cache.Add(snapshot, current);
        old.CameraStations[0].Iso = "800";
        Assert.Same(current, cache.Get(snapshot));
    }

    private static LocalSnapshotItemViewModel Snapshot(Guid? id = null) =>
        new(new LocalSnapshotSummary(id ?? Guid.NewGuid(), "测试存档",
            DateTimeOffset.UtcNow, 100, false, true));

    private static SnapshotInspectorViewModel Inspector() =>
        new("测试存档", DateTimeOffset.UtcNow, []);
}
