using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed class LocalSnapshotItemViewModel(LocalSnapshotSummary snapshot)
{
    public Guid Id { get; } = snapshot.Id;

    public string Name { get; } = snapshot.Name;

    public string CreatedAt { get; } = snapshot.CreatedAt.ToLocalTime().ToString(
        "yyyy-MM-dd HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);

    public string Size { get; } = FormatSize(snapshot.Length);

    public string SyncStatus { get; } = snapshot.UploadEligible
        ? snapshot.Uploaded ? "已同步" : "等待上传"
        : "仅本机";

    public string Detail => $"{Size} · {SyncStatus}";

    public Guid? RoomId { get; }

    public bool IsCloud { get; }

    public bool IsDesktopFile { get; }

    public LocalSnapshotItemViewModel(SnapshotSummary snapshot)
        : this(new LocalSnapshotSummary(
            snapshot.Id,
            snapshot.Name,
            snapshot.CreatedAt,
            snapshot.PackageLength,
            true,
            true))
    {
        RoomId = snapshot.RoomId;
        IsCloud = true;
        SyncStatus = "云端";
    }

    public LocalSnapshotItemViewModel(CombinedSnapshot snapshot, long packageLength)
        : this(new LocalSnapshotSummary(
            snapshot.Id,
            snapshot.Name,
            snapshot.CreatedAt,
            packageLength,
            false,
            false))
    {
        IsDesktopFile = true;
        SyncStatus = "本地文件";
    }

    private static string FormatSize(long length) => length switch
    {
        >= 1024L * 1024L * 1024L => $"{length / (1024d * 1024d * 1024d):0.0} GB",
        >= 1024L * 1024L => $"{length / (1024d * 1024d):0.0} MB",
        >= 1024L => $"{length / 1024d:0.0} KB",
        _ => $"{length} B"
    };
}
