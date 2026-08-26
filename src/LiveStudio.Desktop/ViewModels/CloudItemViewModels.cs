using CommunityToolkit.Mvvm.ComponentModel;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

public sealed partial class CloudRoomItemViewModel(LiveRoomSummary room) : ObservableObject
{
    public Guid Id { get; } = room.Id;

    public Guid? DeviceId { get; } = room.DeviceId;

    public string Name { get; } = room.Name;

    public bool Online { get; } = room.Online;

    public bool HasConfigurationDrift { get; } = room.HasConfigurationDrift;

    public bool CanBatchSelect { get; } = room.Online && room.DeviceId is not null;

    [ObservableProperty]
    public partial bool IsBatchSelected { get; set; }

    public string Status { get; } = room.Online ? "在线" : "离线";

    public string ConfigurationStatus { get; } = room.HasConfigurationDrift ? "配置已漂移" : "配置一致";

    public string LastSnapshot { get; } = room.LastSnapshotAt?.ToLocalTime().ToString(
        "MM-dd HH:mm",
        System.Globalization.CultureInfo.CurrentCulture) ?? "尚无存档";
}

public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(JobSummary job)
    {
        Id = job.Id;
        Kind = job.Kind switch
        {
            JobKind.Capture => "联合保存",
            JobKind.Restore => "应用存档",
            JobKind.RefreshPreview => "刷新画面",
            _ => job.Kind.ToString()
        };
        Source = "云端";
        Status = FormatJobStatus(job.Status);
        Message = job.Message ?? "等待执行";
        SortTime = job.CreatedAt;
        CreatedAt = FormatTime(job.CreatedAt);
    }

    public ActivityItemViewModel(LocalOperationSummary operation)
    {
        Id = operation.Id;
        Kind = operation.Kind == LocalOperationKind.Capture ? "联合保存" : "应用存档";
        Source = "本机";
        Status = operation.Status switch
        {
            LocalOperationStatus.Running => "正在执行",
            LocalOperationStatus.Succeeded => "已完成",
            LocalOperationStatus.Blocked => "预检已阻止",
            LocalOperationStatus.Failed => "失败",
            LocalOperationStatus.FailedRolledBack => "失败，已回滚",
            LocalOperationStatus.RollbackFailed => "回滚失败",
            _ => operation.Status.ToString()
        };
        Message = operation.Message;
        SortTime = operation.StartedAt;
        CreatedAt = FormatTime(operation.StartedAt);
    }

    public Guid Id { get; }

    public string Kind { get; }

    public string Source { get; }

    public string Status { get; }

    public string Message { get; }

    public string CreatedAt { get; }

    public DateTimeOffset SortTime { get; }

    private static string FormatJobStatus(JobStatus status) => status switch
    {
        JobStatus.Queued => "等待领取",
        JobStatus.Claimed => "已领取",
        JobStatus.Preflight => "正在预检",
        JobStatus.BackingUp => "正在备份",
        JobStatus.Capturing => "正在读取",
        JobStatus.RefreshingPreview => "正在刷新画面",
        JobStatus.Packaging => "正在打包",
        JobStatus.Uploading => "正在上传",
        JobStatus.StoppingApplications => "正在停止应用",
        JobStatus.Applying => "正在应用",
        JobStatus.StartingApplications => "正在启动应用",
        JobStatus.Verifying => "正在验证",
        JobStatus.Succeeded => "已完成",
        JobStatus.DeviceOffline => "设备离线",
        JobStatus.BlockedByLiveSession => "历史任务已停止",
        JobStatus.MappingRequired => "需要设备映射",
        JobStatus.UnsupportedDeviceMode => "设备格式不支持",
        JobStatus.MissingFilter => "缺少滤镜",
        JobStatus.MissingAsset => "缺少滤镜素材",
        JobStatus.IncompatibleVersion => "版本不兼容",
        JobStatus.FailedRolledBack => "失败，已回滚",
        JobStatus.RollbackFailed => "回滚失败",
        _ => status.ToString()
    };

    private static string FormatTime(DateTimeOffset value) => value.ToLocalTime().ToString(
        "yyyy-MM-dd HH:mm",
        System.Globalization.CultureInfo.CurrentCulture);
}
