using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed class LocalSnapshotItemViewModel(LocalSnapshotSummary snapshot, string? roomName = null)
{
    public Guid Id { get; } = snapshot.Id;

    public string Name { get; } = snapshot.Name;

    public string DisplayName { get; } = ExtractDisplayName(snapshot.Name, snapshot.CreatedAt);

    public string RoomName { get; private set; } = string.IsNullOrWhiteSpace(roomName)
        ? ExtractRoomName(snapshot.Name, snapshot.RoomId)
        : roomName;

    public DateTimeOffset CreatedAtValue { get; } = snapshot.CreatedAt;

    public string CreatedAt { get; } = snapshot.CreatedAt.ToLocalTime().ToString(
        "yyyy-MM-dd HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);

    public string Time { get; } = snapshot.CreatedAt.ToLocalTime().ToString(
        "HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);

    public DateOnly LocalDate { get; } = DateOnly.FromDateTime(snapshot.CreatedAt.ToLocalTime().DateTime);

    public string Size { get; } = FormatSize(snapshot.Length);

    public string SyncStatus { get; } = "本机";

    public string Detail => Size;

    public Guid? RoomId { get; } = snapshot.RoomId;

    public bool IsCloud { get; }

    public bool IsDesktopFile { get; }

    public bool IsUploadPending { get; }

    public SnapshotSummary? CloudSummary { get; private set; }

    public LocalSnapshotItemViewModel(SnapshotSummary snapshot)
        : this(new LocalSnapshotSummary(
            snapshot.Id,
            snapshot.Name,
            snapshot.CreatedAt,
            snapshot.PackageLength,
            true,
            true,
            snapshot.RoomId))
    {
        RoomId = snapshot.RoomId;
        RoomName = ExtractRoomName(snapshot.Name, snapshot.RoomId);
        IsCloud = true;
        SyncStatus = "云端";
        CloudSummary = snapshot;
    }

    public LocalSnapshotItemViewModel(CombinedSnapshot snapshot, long packageLength)
        : this(new LocalSnapshotSummary(
            snapshot.Id,
            snapshot.Name,
            snapshot.CreatedAt,
            packageLength,
            false,
            false,
            snapshot.RoomId))
    {
        RoomId = snapshot.RoomId;
        RoomName = ExtractRoomName(snapshot.Name, snapshot.RoomId);
        IsDesktopFile = true;
        SyncStatus = "本地文件";
    }

    public void ApplyRoomName(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            RoomName = value;
        }
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

        if (roomId is not { } id || id == Guid.Empty)
        {
            return "未分配直播间";
        }

        var shortId = id.ToString("N")[..8];
        return $"直播间 {shortId}";
    }

    private static string ExtractDisplayName(string name, DateTimeOffset createdAt)
    {
        const string marker = "号直播间";
        var markerIndex = name.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return NormalizeGeneratedName(name, createdAt);
        }

        var suffix = name[(markerIndex + marker.Length)..].Trim(' ', '·', '—', '-');
        return string.IsNullOrWhiteSpace(suffix)
            ? "画面备份"
            : NormalizeGeneratedName(suffix, createdAt);
    }

    private static string NormalizeGeneratedName(string name, DateTimeOffset createdAt)
    {
        var local = createdAt.ToLocalTime();
        var completeTimestamp = local.ToString(
            "yyyy-MM-dd HH:mm",
            System.Globalization.CultureInfo.CurrentCulture);
        var legacyTimeOnly = local.ToString(
            "HH:mm",
            System.Globalization.CultureInfo.CurrentCulture);

        if (string.Equals(name, legacyTimeOnly, StringComparison.Ordinal)
            || string.Equals(name, $"{completeTimestamp} 画面存档", StringComparison.Ordinal))
        {
            return completeTimestamp;
        }

        return name;
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

    public string Time => FormatRelativeTime(Snapshot.CreatedAtValue);

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

    private static string FormatRelativeTime(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.Now - value.ToLocalTime();
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
        }

        return elapsed < TimeSpan.FromDays(7)
            ? $"{Math.Max(1, (int)elapsed.TotalDays)} 天前"
            : value.ToLocalTime().ToString("M月d日", System.Globalization.CultureInfo.CurrentCulture);
    }
}
