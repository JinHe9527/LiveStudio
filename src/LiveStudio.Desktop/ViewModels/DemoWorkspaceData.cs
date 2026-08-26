using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using LiveStudio.Contracts;

namespace LiveStudio.Desktop.ViewModels;

internal static class DemoWorkspaceData
{
    private static readonly Guid OrganizationId = Guid.Parse("de000000-0000-4000-8000-000000000001");

    public static OrganizationSummary Organization { get; } = new(OrganizationId, "LiveStudio 演示 Organization");

    public static IReadOnlyList<LiveRoomSummary> CreateRooms() => Enumerable.Range(1, 14)
        .Select(roomNumber => new LiveRoomSummary(
            CreateGuid(roomNumber, 1),
            OrganizationId,
            $"{roomNumber}号直播间",
            CreateGuid(roomNumber, 2),
            DateTimeOffset.Now.AddMinutes(-roomNumber * 7),
            roomNumber is not 12 and not 14,
            roomNumber is 4 or 9))
        .ToArray();

    public static IReadOnlyList<JobSummary> CreateJobs()
    {
        var statuses = new[]
        {
            JobStatus.Succeeded,
            JobStatus.Succeeded,
            JobStatus.IncompatibleVersion,
            JobStatus.FailedRolledBack,
            JobStatus.Verifying,
            JobStatus.DeviceOffline,
            JobStatus.MappingRequired
        };
        return Enumerable.Range(0, 42).Select(index =>
        {
            var roomNumber = index % 14 + 1;
            var kind = (index % 3) switch
            {
                0 => JobKind.Capture,
                1 => JobKind.Restore,
                _ => JobKind.RefreshPreview
            };
            var status = statuses[index % statuses.Length];
            return new JobSummary(
                CreateGuid(roomNumber, 500 + index),
                CreateGuid(roomNumber, 1),
                CreateGuid(roomNumber, 2),
                kind == JobKind.Restore ? CreateGuid(roomNumber, 1000 + index) : null,
                kind,
                status,
                CompatibilityLevel.Experimental,
                DateTimeOffset.Now.AddHours(-index * 3),
                IsTerminal(status) ? DateTimeOffset.Now.AddHours(-index * 3).AddMinutes(2) : null,
                CreateJobMessage(roomNumber, kind, status),
                status is JobStatus.FailedRolledBack ? "ReadbackMismatch" : null);
        }).ToArray();
    }

    public static JobSummary CreateInteractiveJob(int roomNumber, JobKind kind, JobStatus status, string message) => new(
        Guid.NewGuid(),
        CreateGuid(roomNumber, 1),
        CreateGuid(roomNumber, 2),
        null,
        kind,
        status,
        CompatibilityLevel.Experimental,
        DateTimeOffset.Now,
        IsTerminal(status) ? DateTimeOffset.Now : null,
        message,
        null);

    public static LocalMappingContext CreateMappingContext(Guid snapshotId, int roomNumber)
    {
        var mode = (roomNumber % 3) switch
        {
            0 => new VideoMode(3840, 2160, 30, 1, "NV12", "709", "Limited"),
            1 => new VideoMode(1920, 1080, 60, 1, "NV12", "709", "Limited"),
            _ => new VideoMode(1920, 1080, 50, 1, "YUY2", "709", "Full")
        };
        var deviceName = roomNumber % 2 == 0
            ? "AVerMedia Live Gamer Ultra 2.1"
            : "Elgato Cam Link 4K";
        var obsSourceId = CreateGuid(roomNumber, 10);
        var liveCompanionSourceId = CreateGuid(roomNumber, 20);
        var deviceId = CreateGuid(roomNumber, 2);
        var obsMapping = new DeviceMapping(
            CreateGuid(roomNumber, 80),
            OrganizationId,
            deviceId,
            obsSourceId,
            ApplicationKind.Obs,
            $"demo-obs-device-{roomNumber:00}",
            $"OBS 主机位 {roomNumber:00}",
            "主画面",
            false);
        var sources = new[]
        {
            new LocalMappingSource(obsSourceId, ApplicationKind.Obs, $"{roomNumber}号直播间主机位", deviceName, mode, obsMapping),
            new LocalMappingSource(liveCompanionSourceId, ApplicationKind.LiveCompanion, $"{roomNumber}号直播间直播伴侣机位", deviceName, mode, null)
        };
        var targets = new[]
        {
            new LocalMappingTarget(ApplicationKind.Obs, $"OBS 主机位 {roomNumber:00}", $"demo-obs-device-{roomNumber:00}", deviceName, mode),
            new LocalMappingTarget(ApplicationKind.Obs, "OBS 备用采集卡", "demo-obs-spare", "Blackmagic DeckLink Mini Recorder 4K", new VideoMode(1920, 1080, 30, 1, "NV12", "709", "Limited")),
            new LocalMappingTarget(ApplicationKind.LiveCompanion, $"直播伴侣主机位 {roomNumber:00}", $"demo-live-device-{roomNumber:00}", deviceName, mode),
            new LocalMappingTarget(ApplicationKind.LiveCompanion, "直播伴侣备用机位", "demo-live-spare", "Magewell USB Capture HDMI Gen 2", new VideoMode(1920, 1080, 30, 1, "NV12", "709", "Limited"))
        };
        return new LocalMappingContext(snapshotId, sources, targets);
    }

    public static Bitmap CreatePreview(int roomNumber, ApplicationKind application)
    {
        const int width = 960;
        const int height = 540;
        var content = CreateBitmapContent(width, height, roomNumber, application);
        return new Bitmap(new MemoryStream(content, writable: false));
    }

    public static int GetRoomNumber(string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return 1;
        }

        var digits = new string(roomName.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var roomNumber) && roomNumber is >= 1 and <= 14
            ? roomNumber
            : 1;
    }

    public static Guid CreateGuid(int roomNumber, int itemNumber)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = 0xDE;
        bytes[1] = 0x10;
        bytes[2] = (byte)roomNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[3..5], checked((ushort)itemNumber));
        bytes[6] = 0x40;
        bytes[8] = 0x80;
        return new Guid(bytes);
    }

    private static byte[] CreateBitmapContent(int width, int height, int roomNumber, ApplicationKind application)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var pixelLength = rowSize * height;
        var content = new byte[54 + pixelLength];
        content[0] = (byte)'B';
        content[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(2, 4), content.Length);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(content.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(content.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(34, 4), pixelLength);

        var accent = application == ApplicationKind.Obs
            ? (R: 38 + roomNumber * 3, G: 86 + roomNumber * 2, B: 138 + roomNumber * 2)
            : (R: 126 + roomNumber * 2, G: 56 + roomNumber * 2, B: 82 + roomNumber * 2);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var depth = 1d - y / (double)height;
                var red = (byte)Math.Clamp(18 + accent.R * depth * 0.34, 0, 255);
                var green = (byte)Math.Clamp(20 + accent.G * depth * 0.30, 0, 255);
                var blue = (byte)Math.Clamp(24 + accent.B * depth * 0.36, 0, 255);
                if (x is > 90 and < 245 || x is > 715 and < 870)
                {
                    red = (byte)Math.Min(255, red + 22);
                    green = (byte)Math.Min(255, green + 18);
                    blue = (byte)Math.Min(255, blue + 12);
                }

                if (y < 125 && x is > 310 and < 650)
                {
                    red = (byte)Math.Min(255, red + 18);
                    green = (byte)Math.Min(255, green + 18);
                    blue = (byte)Math.Min(255, blue + 20);
                }

                if (y is > 330 and < 405 && x is > 210 and < 750)
                {
                    red = 48;
                    green = 42;
                    blue = 39;
                }

                var centerX = x - 480;
                var centerY = y - 250;
                if (centerX * centerX + centerY * centerY < 45 * 45)
                {
                    red = 195;
                    green = 152;
                    blue = 128;
                }
                else if (x is > 410 and < 550 && y is > 285 and < 455)
                {
                    red = (byte)(application == ApplicationKind.Obs ? 36 : 74);
                    green = (byte)(application == ApplicationKind.Obs ? 58 : 38);
                    blue = (byte)(application == ApplicationKind.Obs ? 78 : 58);
                }

                var offset = 54 + (height - 1 - y) * rowSize + x * 3;
                content[offset] = blue;
                content[offset + 1] = green;
                content[offset + 2] = red;
            }
        }

        return content;
    }

    private static bool IsTerminal(JobStatus status) => status is JobStatus.Succeeded
        or JobStatus.DeviceOffline
        or JobStatus.BlockedByLiveSession
        or JobStatus.MappingRequired
        or JobStatus.UnsupportedDeviceMode
        or JobStatus.MissingFilter
        or JobStatus.MissingAsset
        or JobStatus.IncompatibleVersion
        or JobStatus.FailedRolledBack
        or JobStatus.RollbackFailed;

    private static string CreateJobMessage(int roomNumber, JobKind kind, JobStatus status) => status switch
    {
        JobStatus.Succeeded => $"{roomNumber}号直播间逐字段回读完成",
        JobStatus.BlockedByLiveSession => $"{roomNumber}号直播间存在历史终止记录",
        JobStatus.FailedRolledBack => $"{roomNumber}号直播间字段不一致，原配置已回滚",
        JobStatus.Verifying => $"{roomNumber}号直播间正在核对设备、滤镜和美颜字段",
        JobStatus.DeviceOffline => $"{roomNumber}号直播间执行端离线，任务未排队",
        JobStatus.MappingRequired => $"{roomNumber}号直播间需要确认目标采集卡映射",
        _ => $"{roomNumber}号直播间 {kind}"
    };
}
