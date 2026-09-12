using System.ComponentModel;

namespace LiveStudio.Desktop.ViewModels;

// 仅用于当前桌面会话的展示；恢复仍由 Agent 重新读取并验证原始存档。
internal sealed class SnapshotInspectorCache(int capacity = 3)
{
    private readonly LinkedList<Entry> entries = new();

    public SnapshotInspectorViewModel? Get(LocalSnapshotItemViewModel snapshot)
    {
        var node = entries.First;
        while (node is not null)
        {
            if (ReferenceEquals(node.Value.Snapshot, snapshot))
            {
                entries.Remove(node);
                entries.AddFirst(node);
                return node.Value.Inspector;
            }
            node = node.Next;
        }
        return null;
    }

    public void Add(LocalSnapshotItemViewModel snapshot, SnapshotInspectorViewModel inspector)
    {
        Remove(snapshot.Id);
        PropertyChangedEventHandler changed = (_, _) => Remove(snapshot.Id);
        foreach (var station in inspector.CameraStations)
        {
            station.PropertyChanged += changed;
        }
        entries.AddFirst(new Entry(snapshot, inspector, changed));
        while (entries.Count > capacity)
        {
            Remove(entries.Last!.Value.Snapshot.Id);
        }
    }

    public void Clear()
    {
        while (entries.Last is { } node)
        {
            Remove(node.Value.Snapshot.Id);
        }
    }

    private void Remove(Guid id)
    {
        var node = entries.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Snapshot.Id == id)
            {
                foreach (var station in node.Value.Inspector.CameraStations)
                {
                    station.PropertyChanged -= node.Value.Changed;
                }
                entries.Remove(node);
            }
            node = next;
        }
    }

    private sealed record Entry(LocalSnapshotItemViewModel Snapshot,
        SnapshotInspectorViewModel Inspector, PropertyChangedEventHandler Changed);
}
