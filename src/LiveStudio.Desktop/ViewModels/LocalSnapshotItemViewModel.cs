using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed class LocalSnapshotItemViewModel(LocalSnapshotSummary snapshot)
{
    public Guid Id { get; } = snapshot.Id;

    public string Name { get; } = snapshot.Name;

    public string DisplayName { get; } = ExtractDisplayName(snapshot.Name);

    public string RoomName { get; private set; } = ExtractRoomName(snapshot.Name, null);

    public DateTimeOffset CreatedAtValue { get; } = snapshot.CreatedAt;

    public string CreatedAt { get; } = snapshot.CreatedAt.ToLocalTime().ToString(
        "yyyy-MM-dd HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);

    public string Time { get; } = snapshot.CreatedAt.ToLocalTime().ToString(
        "HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);

    public DateOnly LocalDate { get; } = DateOnly.FromDateTime(snapshot.CreatedAt.ToLocalTime().DateTime);

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
        RoomName = ExtractRoomName(snapshot.Name, snapshot.RoomId);
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
        RoomId = snapshot.RoomId;
        RoomName = ExtractRoomName(snapshot.Name, snapshot.RoomId);
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

    private static string ExtractRoomName(string name, Guid? roomId)
    {
        const string marker = "号直播间";
        var markerIndex = name.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var start = markerIndex;
            while (start > 0 && char.IsDigit(name[start - 1]))
            {
                start--;
            }

            if (start < markerIndex)
            {
                return name[start..(markerIndex + marker.Length)];
            }
        }

        if (roomId is not { } id)
        {
            return "未分配直播间";
        }

        var shortId = id.ToString("N")[..8];
        return $"直播间 {shortId}";
    }

    private static string ExtractDisplayName(string name)
    {
        const string marker = "号直播间";
        var markerIndex = name.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return name;
        }

        var suffix = name[(markerIndex + marker.Length)..].Trim(' ', '·', '—', '-');
        return string.IsNullOrWhiteSpace(suffix) ? "画面备份" : suffix;
    }
}

public sealed class SnapshotRoomFilterItemViewModel(Guid? roomId, string name, int snapshotCount, int sortOrder)
{
    public Guid? RoomId { get; } = roomId;

    public string Name { get; } = name;

    public int SnapshotCount { get; } = snapshotCount;

    public int SortOrder { get; } = sortOrder;

    public string Summary => $"{SnapshotCount} 份存档";

    public string Display => $"{Name} · {SnapshotCount} 份";
}

public sealed class SnapshotTimelineItemViewModel(LocalSnapshotItemViewModel snapshot, bool startsDateGroup)
{
    public LocalSnapshotItemViewModel Snapshot { get; } = snapshot;

    public bool StartsDateGroup { get; } = startsDateGroup;

    public string DateHeader { get; } = FormatDate(snapshot.CreatedAtValue);

    public string Name => Snapshot.DisplayName;

    public string Detail => Snapshot.Detail;

    public string Time => Snapshot.Time;

    private static string FormatDate(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var date = local.Date;
        var today = DateTimeOffset.Now.Date;
        var prefix = date == today
            ? "今天"
            : date == today.AddDays(-1)
                ? "昨天"
                : local.ToString("M月d日", System.Globalization.CultureInfo.CurrentCulture);
        return $"{prefix} · {local:dddd}";
    }
}
