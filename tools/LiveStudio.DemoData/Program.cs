using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Packaging;

namespace LiveStudio.DemoData;

internal static class Program
{
    private const int RoomCount = 14;
    private const int DaysPerRoom = 14;
    private const int BackupsPerDay = 2;
    private const int SchemaVersion = 2;
    private const string DemoKeyId = "livestudio-demo-data";
    private const string NativeDocumentPath = "native/live-companion/settings.json";

    private static readonly Guid OrganizationId = Guid.Parse("de000000-0000-4000-8000-000000000001");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var outputDirectory = ParseOutputDirectory(args);
            Directory.CreateDirectory(outputDirectory);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var createdPaths = new List<string>();

            var localToday = new DateTimeOffset(DateTimeOffset.Now.Date, DateTimeOffset.Now.Offset);
            for (var roomNumber = 1; roomNumber <= RoomCount; roomNumber++)
            {
                for (var dayOffset = 0; dayOffset < DaysPerRoom; dayOffset++)
                {
                    for (var backupIndex = 0; backupIndex < BackupsPerDay; backupIndex++)
                    {
                        var package = CreatePackage(roomNumber, dayOffset, backupIndex, localToday);
                        var targetPath = Path.Combine(outputDirectory, $"{package.Snapshot.Id:N}.lscfg");
                        if (File.Exists(targetPath))
                        {
                            continue;
                        }

                        await SnapshotPackageWriter.WriteAsync(
                            targetPath,
                            package.Snapshot,
                            package.Files,
                            signingKey,
                            DemoKeyId,
                            CancellationToken.None);
                        var inspection = await SnapshotPackageReader.InspectAsync(targetPath, CancellationToken.None);
                        if (inspection.Package.Snapshot.Id != package.Snapshot.Id
                            || inspection.Package.Snapshot.Applications.Count != 2)
                        {
                            throw new InvalidDataException($"演示存档回读失败：{targetPath}");
                        }

                        createdPaths.Add(targetPath);
                    }
                }

                Console.WriteLine($"已准备 {roomNumber}号直播间：{DaysPerRoom} 天·每天 {BackupsPerDay} 份");
            }

            Console.WriteLine($"演示存档库：{outputDirectory}");
            Console.WriteLine($"本次新增 {createdPaths.Count} 份，目标 {RoomCount} 个直播间·{DaysPerRoom} 天·每天 {BackupsPerDay} 份。");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or SnapshotPackageException
            or CryptographicException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string ParseOutputDirectory(string[] args)
    {
        if (args.Length == 0)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveStudio",
                "SnapshotLibrary");
        }

        if (args.Length == 2 && string.Equals(args[0], "--output", StringComparison.Ordinal))
        {
            return Path.GetFullPath(args[1]);
        }

        throw new ArgumentException("用法：LiveStudio.DemoData [--output <存档目录>]");
    }

    private static DemoPackage CreatePackage(
        int roomNumber,
        int dayOffset,
        int backupIndex,
        DateTimeOffset localToday)
    {
        var roomId = CreateGuid(roomNumber, 1);
        var snapshotId = CreateGuid(roomNumber, 1000 + dayOffset * BackupsPerDay + backupIndex);
        var createdAt = localToday
            .AddDays(-dayOffset)
            .AddHours(backupIndex == 0 ? 11 : 20)
            .AddMinutes((roomNumber * 7 + backupIndex * 13 + dayOffset * 3) % 50);
        var variation = roomNumber + dayOffset * 2 + backupIndex * 3;
        var mode = CreateVideoMode(variation);
        var device = CreateCaptureDevice(roomNumber, mode);
        var files = new List<PackageFile>();
        var assets = new List<AssetBlob>();
        var obsSource = CreateObsSource(roomNumber, variation, device, mode, files, assets);
        var liveCompanionData = CreateLiveCompanionData(roomNumber, variation, device, mode, files, assets);
        var obsCoverage = CreateObsCoverage(obsSource);
        var obs = new ApplicationSnapshot(
            ApplicationKind.Obs,
            variation % 2 == 0 ? "31.0.3" : "31.1.2",
            "demo-obs-websocket",
            HashText("demo-obs-adapter"),
            HashText("demo-obs-structure"),
            CompatibilityLevel.Experimental,
            roomNumber % 4 != 0,
            obsCoverage,
            [obsSource],
            []);
        var liveCompanion = new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            variation % 3 == 0 ? "8.2.0-demo" : "8.1.0-demo",
            "demo-live-companion-readonly",
            HashText("demo-live-companion-adapter"),
            HashText("demo-live-companion-structure"),
            CompatibilityLevel.Experimental,
            roomNumber % 5 != 0,
            liveCompanionData.Coverage,
            [liveCompanionData.Source],
            [liveCompanionData.Document]);
        var snapshot = new CombinedSnapshot(
            snapshotId,
            OrganizationId,
            roomId,
            $"{roomNumber}号直播间·{(backupIndex == 0 ? "午间备份" : "晚间备份")}",
            createdAt,
            SchemaVersion,
            [obs, liveCompanion],
            assets,
            []);
        return new DemoPackage(snapshot, files);
    }

    private static VideoMode CreateVideoMode(int roomNumber) => (roomNumber % 4) switch
    {
        0 => new VideoMode(3840, 2160, 30, 1, "NV12", "709", "Limited"),
        1 => new VideoMode(1920, 1080, 60, 1, "NV12", "709", "Limited"),
        2 => new VideoMode(1920, 1080, 50, 1, "YUY2", "709", "Full"),
        _ => new VideoMode(2560, 1440, 30, 1, "MJPEG", "sRGB", "Full")
    };

    private static CaptureDeviceDescriptor CreateCaptureDevice(int roomNumber, VideoMode mode)
    {
        var descriptor = (roomNumber % 4) switch
        {
            0 => ("Blackmagic DeckLink Mini Recorder 4K", "1EDB", "BE56"),
            1 => ("Elgato Cam Link 4K", "0FD9", "0066"),
            2 => ("AVerMedia Live Gamer Ultra 2.1", "07CA", "3138"),
            _ => ("Magewell USB Capture HDMI Gen 2", "2935", "0001")
        };
        var supportedModes = new[]
        {
            mode,
            new VideoMode(1920, 1080, 30, 1, "NV12", "709", "Limited"),
            new VideoMode(1280, 720, 60, 1, "NV12", "709", "Limited")
        }.Distinct().ToArray();
        return new CaptureDeviceDescriptor(
            descriptor.Item1,
            descriptor.Item2,
            descriptor.Item3,
            null,
            $"demo-capture-slot-{roomNumber:00}",
            supportedModes);
    }

    private static VideoSource CreateObsSource(
        int roomNumber,
        int variation,
        CaptureDeviceDescriptor device,
        VideoMode mode,
        List<PackageFile> files,
        List<AssetBlob> assets)
    {
        var sourceId = CreateGuid(roomNumber, 10);
        var lut = AddAsset(roomNumber, "obs-cinema.cube", CreateLutContent(variation), "text/plain", files, assets);
        var mask = AddAsset(roomNumber, "rounded-camera-mask.png", CreateDemoImageContent(variation), "image/png", files, assets);
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["video_device_id"] = Json($"demo-device-{roomNumber:00}"),
            ["video_device_name"] = Json(device.FriendlyName),
            ["res_type"] = Json(1),
            ["resolution"] = Json($"{mode.Width}x{mode.Height}"),
            ["fps_num"] = Json(mode.FramesPerSecondNumerator),
            ["fps_den"] = Json(mode.FramesPerSecondDenominator),
            ["video_format"] = Json(mode.PixelFormat),
            ["color_space"] = Json(mode.ColorSpace),
            ["color_range"] = Json(mode.ColorRange),
            ["buffering"] = Json(variation % 3 == 0)
        };
        var filters = new[]
        {
            new VideoFilter(
                CreateGuid(roomNumber, 11),
                "直播间基础 LUT",
                "clut_filter",
                true,
                0,
                new Dictionary<string, JsonElement>
                {
                    ["amount"] = Json(0.62 + variation % 18 * 0.01),
                    ["image_path"] = Json(lut.ReferencePath)
                },
                [lut]),
            new VideoFilter(
                CreateGuid(roomNumber, 12),
                "色彩校正",
                "color_filter_v2",
                true,
                1,
                new Dictionary<string, JsonElement>
                {
                    ["brightness"] = Json(-0.03 + variation % 16 * 0.002),
                    ["contrast"] = Json(0.08 + variation % 18 * 0.004),
                    ["saturation"] = Json(0.04 + variation % 20 * 0.003),
                    ["gamma"] = Json(0.01 * (variation % 5)),
                    ["hue_shift"] = Json(variation % 15 - 7)
                },
                []),
            new VideoFilter(
                CreateGuid(roomNumber, 13),
                "细节锐化",
                "sharpness_filter_v2",
                variation % 6 != 0,
                2,
                new Dictionary<string, JsonElement>
                {
                    ["sharpness"] = Json(0.18 + variation % 20 * 0.006)
                },
                []),
            new VideoFilter(
                CreateGuid(roomNumber, 14),
                "圆角镜头遮罩",
                "mask_filter_v2",
                variation % 2 == 0,
                3,
                new Dictionary<string, JsonElement>
                {
                    ["image_path"] = Json(mask.ReferencePath),
                    ["opacity"] = Json(0.94)
                },
                [mask])
        };
        return new VideoSource(
            sourceId,
            $"{roomNumber}号直播间主机位",
            "av_capture_input",
            device,
            mode,
            settings,
            filters);
    }

    private static LiveCompanionData CreateLiveCompanionData(
        int roomNumber,
        int variation,
        CaptureDeviceDescriptor device,
        VideoMode mode,
        List<PackageFile> files,
        List<AssetBlob> assets)
    {
        var sourceId = CreateGuid(roomNumber, 20);
        var makeup = AddAsset(roomNumber, "studio-makeup.bundle", CreateMakeupContent(variation), "application/octet-stream", files, assets);
        var lut = AddAsset(roomNumber, "skin-tone.cube", CreateLutContent(variation + 20), "text/plain", files, assets);
        var curves = CreateCurves(variation);
        var skinSettings = new Dictionary<string, JsonElement>
        {
            ["enabled"] = Json(true),
            ["presetId"] = Json($"demo-natural-{(variation % 4) + 1}"),
            ["smoothness"] = Json(0.35 + variation % 20 * 0.015),
            ["whitening"] = Json(0.22 + variation % 18 * 0.01),
            ["rosy"] = Json(0.12 + variation % 16 * 0.007),
            ["skinTone"] = Json(variation % 3 - 1),
            ["clarity"] = Json(0.18 + variation % 20 * 0.006)
        };
        var reshapeSettings = new Dictionary<string, JsonElement>
        {
            ["faceSlim"] = Json(0.18 + variation % 18 * 0.008),
            ["cheekbone"] = Json(0.08 + variation % 16 * 0.004),
            ["jaw"] = Json(0.12 + variation % 18 * 0.005),
            ["chin"] = Json(-0.02 + variation % 14 * 0.003),
            ["forehead"] = Json(0.03 + variation % 15 * 0.002),
            ["eyeSize"] = Json(0.08 + variation % 18 * 0.004),
            ["eyeDistance"] = Json(0.01 * (variation % 4)),
            ["noseWidth"] = Json(0.06 + variation % 17 * 0.003),
            ["mouthShape"] = Json(0.04 + variation % 16 * 0.002)
        };
        var makeupSettings = new Dictionary<string, JsonElement>
        {
            ["materialId"] = Json($"demo-makeup-{roomNumber:00}"),
            ["style"] = Json(variation % 2 == 0 ? "清透直播" : "自然棚拍"),
            ["lipColor"] = Json(variation % 2 == 0 ? "#B95C62" : "#A94E58"),
            ["intensity"] = Json(0.28 + variation % 18 * 0.012),
            ["opacity"] = Json(0.72 + variation % 20 * 0.008),
            ["path"] = Json(makeup.ReferencePath)
        };
        var filters = new[]
        {
            new VideoFilter(CreateGuid(roomNumber, 21), "美肤", "live_companion_beauty_skin", true, 0, skinSettings, []),
            new VideoFilter(CreateGuid(roomNumber, 22), "脸型与五官", "live_companion_face_reshape", true, 1, reshapeSettings, []),
            new VideoFilter(CreateGuid(roomNumber, 23), "直播美妆", "live_companion_makeup", true, 2, makeupSettings, [makeup]),
            new VideoFilter(
                CreateGuid(roomNumber, 24),
                "RGB 曲线",
                "live_companion_rgb_curves",
                variation % 5 != 0,
                3,
                new Dictionary<string, JsonElement>
                {
                    ["curves"] = curves,
                    ["interpolation"] = Json("cubic")
                },
                []),
            new VideoFilter(
                CreateGuid(roomNumber, 25),
                "肤色 LUT",
                "color_grade_filter",
                true,
                4,
                new Dictionary<string, JsonElement>
                {
                    ["amount"] = Json(0.44 + variation % 20 * 0.01),
                    ["path"] = Json(lut.ReferencePath)
                },
                [lut])
        };
        var sourceSettings = new Dictionary<string, JsonElement>
        {
            ["captureDevice"] = Json(device.FriendlyName),
            ["deviceId"] = Json($"demo-device-{roomNumber:00}"),
            ["resolution"] = Json($"{mode.Width}x{mode.Height}"),
            ["frameRate"] = Json(mode.FramesPerSecondNumerator / (double)mode.FramesPerSecondDenominator),
            ["pixelFormat"] = Json(mode.PixelFormat),
            ["colorSpace"] = Json(mode.ColorSpace),
            ["colorRange"] = Json(mode.ColorRange)
        };
        var source = new VideoSource(
            sourceId,
            $"{roomNumber}号直播间直播伴侣机位",
            "live_companion_video_source",
            device,
            mode,
            sourceSettings,
            filters);

        var nativeValues = CreateNativeValues(roomNumber, device, mode, skinSettings, reshapeSettings, makeupSettings, curves, filters);
        var nativeJson = JsonSerializer.SerializeToUtf8Bytes(
            nativeValues.ToDictionary(value => value.JsonPointer, value => value.Value),
            JsonOptions);
        files.Add(new PackageFile(NativeDocumentPath, "application/json", nativeJson));
        var document = new NativeConfigurationDocument(
            "demo-main",
            "JsonFile",
            "demo-structure-1",
            "演示配置文件整体",
            NativeDocumentPath,
            Convert.ToHexStringLower(SHA256.HashData(nativeJson)),
            sourceId,
            nativeValues);
        var coverage = nativeValues.Select(value => new CapturedParameterField(
            $"{NativeDocumentPath}:{value.JsonPointer}",
            value.Category,
            value.Value.ValueKind.ToString(),
            true,
            false,
            "DiscoveryReadOnly")).ToArray();
        return new LiveCompanionData(source, document, coverage);
    }

    private static List<NativeConfigurationValue> CreateNativeValues(
        int roomNumber,
        CaptureDeviceDescriptor device,
        VideoMode mode,
        IReadOnlyDictionary<string, JsonElement> skin,
        IReadOnlyDictionary<string, JsonElement> reshape,
        IReadOnlyDictionary<string, JsonElement> makeup,
        JsonElement curves,
        IReadOnlyList<VideoFilter> filters)
    {
        var values = new List<NativeConfigurationValue>
        {
            Native("/video/deviceId", "DeviceSelection", $"demo-device-{roomNumber:00}"),
            Native("/video/deviceName", "DeviceSelection", device.FriendlyName),
            Native("/video/width", "Width", mode.Width),
            Native("/video/height", "Height", mode.Height),
            Native("/video/fpsNumerator", "FramesPerSecond", mode.FramesPerSecondNumerator),
            Native("/video/fpsDenominator", "FramesPerSecond", mode.FramesPerSecondDenominator),
            Native("/video/pixelFormat", "PixelFormat", mode.PixelFormat),
            Native("/video/colorSpace", "ColorSpace", mode.ColorSpace),
            Native("/video/colorRange", "ColorRange", mode.ColorRange),
            Native("/beauty/skin", "FilterSetting", skin),
            Native("/beauty/reshape", "FilterSetting", reshape),
            Native("/beauty/makeup", "FilterSetting", makeup),
            new NativeConfigurationValue("/beauty/curves", "FilterSetting", curves.Clone()),
            Native("/beauty/filterOrder", "FilterOrder", filters.Select(filter => filter.LogicalId.ToString("N")).ToArray()),
            Native("/beauty/filterEnabled", "FilterEnabled", filters.ToDictionary(filter => filter.LogicalId.ToString("N"), filter => filter.Enabled))
        };
        return values;
    }

    private static JsonElement CreateCurves(int roomNumber)
    {
        var offset = roomNumber * 0.002;
        return Json(new
        {
            master = new
            {
                enabled = true,
                interpolation = "cubic",
                points = new[]
                {
                    new { x = 0.0, y = 0.0 },
                    new { x = 0.22, y = 0.20 + offset },
                    new { x = 0.50, y = 0.53 + offset },
                    new { x = 0.78, y = 0.82 + offset },
                    new { x = 1.0, y = 1.0 }
                }
            },
            red = new
            {
                enabled = true,
                interpolation = "linear",
                points = new[]
                {
                    new { x = 0.0, y = 0.0 },
                    new { x = 0.50, y = 0.51 + offset },
                    new { x = 1.0, y = 1.0 }
                }
            },
            green = new
            {
                enabled = true,
                interpolation = "linear",
                points = new[]
                {
                    new { x = 0.0, y = 0.0 },
                    new { x = 0.50, y = 0.49 + offset },
                    new { x = 1.0, y = 1.0 }
                }
            },
            blue = new
            {
                enabled = roomNumber % 4 != 0,
                interpolation = "linear",
                points = new[]
                {
                    new { x = 0.0, y = 0.0 },
                    new { x = 0.50, y = 0.48 + offset },
                    new { x = 1.0, y = 1.0 }
                }
            }
        });
    }

    private static List<CapturedParameterField> CreateObsCoverage(VideoSource source)
    {
        var fields = new List<CapturedParameterField>();
        foreach (var setting in source.Settings)
        {
            fields.Add(new CapturedParameterField(
                $"/sources/{source.LogicalId:N}/settings/{EscapePointerSegment(setting.Key)}",
                SourceCategory(setting.Key),
                setting.Value.ValueKind.ToString(),
                true,
                true,
                "ObsWebSocketReadback"));
        }

        foreach (var filter in source.Filters)
        {
            var prefix = $"/sources/{source.LogicalId:N}/filters/{filter.LogicalId:N}";
            fields.Add(new CapturedParameterField($"{prefix}/kind", "FilterType", "String", true, true, "ObsWebSocketReadback"));
            fields.Add(new CapturedParameterField($"{prefix}/enabled", "FilterEnabled", "Boolean", true, true, "ObsWebSocketReadback"));
            fields.Add(new CapturedParameterField($"{prefix}/order", "FilterOrder", "Number", true, true, "ObsWebSocketReadback"));
            foreach (var setting in filter.Settings)
            {
                fields.Add(new CapturedParameterField(
                    $"{prefix}/settings/{EscapePointerSegment(setting.Key)}",
                    "FilterSetting",
                    setting.Value.ValueKind.ToString(),
                    true,
                    true,
                    "ObsWebSocketReadback"));
            }
        }

        return fields;
    }

    private static string SourceCategory(string settingName) => settingName switch
    {
        "video_device_id" or "video_device_name" => "DeviceSelection",
        "resolution" => "Width",
        "fps_num" or "fps_den" => "FramesPerSecond",
        "video_format" => "PixelFormat",
        "color_space" => "ColorSpace",
        "color_range" => "ColorRange",
        _ => "VideoSource"
    };

    private static AssetBinding AddAsset(
        int roomNumber,
        string fileName,
        byte[] content,
        string mediaType,
        List<PackageFile> files,
        List<AssetBlob> assets)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var packagePath = $"assets/{sha256}/{fileName}";
        if (assets.All(asset => !string.Equals(asset.Sha256, sha256, StringComparison.Ordinal)))
        {
            assets.Add(new AssetBlob(sha256, mediaType, content.Length, packagePath));
            files.Add(new PackageFile(packagePath, mediaType, content));
        }

        return new AssetBinding(
            CreateGuid(roomNumber, 100 + assets.Count),
            sha256,
            fileName,
            $"C:\\LiveStudioDemo\\Assets\\{roomNumber:00}\\{fileName}",
            $"%LocalAppData%\\LiveStudio\\Assets\\{sha256}\\{fileName}");
    }

    private static NativeConfigurationValue Native<T>(string pointer, string category, T value) =>
        new(pointer, category, Json(value));

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static string HashText(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    private static Guid CreateGuid(int roomNumber, int itemNumber)
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

    private static byte[] CreateLutContent(int seed) => Encoding.UTF8.GetBytes($"""
        TITLE "LiveStudio Demo {seed:00}"
        LUT_3D_SIZE 2
        DOMAIN_MIN 0.0 0.0 0.0
        DOMAIN_MAX 1.0 1.0 1.0
        0.0 0.0 0.0
        0.0 0.0 1.0
        0.0 1.0 0.0
        0.0 1.0 1.0
        1.0 0.0 0.0
        1.0 0.0 1.0
        1.0 1.0 0.0
        1.0 1.0 1.0
        """);

    private static byte[] CreateDemoImageContent(int seed) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x44, 0x45, 0x4D, 0x4F, (byte)seed
    ];

    private static byte[] CreateMakeupContent(int seed) => Encoding.UTF8.GetBytes($"demo-makeup-material-{seed:00}");

    private sealed record DemoPackage(CombinedSnapshot Snapshot, IReadOnlyList<PackageFile> Files);

    private sealed record LiveCompanionData(
        VideoSource Source,
        NativeConfigurationDocument Document,
        IReadOnlyList<CapturedParameterField> Coverage);
}
