using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

internal sealed record VideoDeviceProbeRequest(string DeviceId, VideoMode Mode);
internal sealed record VideoDeviceProbeResult(bool IsSupported, string Message);

// Read-only DirectShow enumeration. Never calls SetFormat, connects pins, or runs a graph.
// Executed in a bounded child process so a faulty device driver cannot hang the Agent.
internal static class WindowsVideoDeviceProbe
{
    internal static VideoDeviceProbeResult Inspect(VideoDeviceProbeRequest request)
    {
        var devices = new List<(IMoniker Moniker, string Name, string Path)>();
        object? enumeratorObject = null;
        IEnumMoniker? enumerator = null;
        try
        {
            enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"), throwOnError: true)!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            var status = ((ICreateDevEnum)enumeratorObject!).CreateClassEnumerator(ref category, out enumerator, 0);
            if (status != 0 || enumerator is null)
            {
                return new(false, "Windows 没有枚举到视频采集设备");
            }
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, nint.Zero) == 0)
            {
                var moniker = monikers[0];
                object? bagObject = null;
                try
                {
                    var bagId = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
                    moniker.BindToStorage(null!, null!, ref bagId, out bagObject);
                    var bag = (IPropertyBag)bagObject;
                    devices.Add((moniker, ReadProperty(bag, "FriendlyName"), ReadProperty(bag, "DevicePath")));
                }
                catch
                {
                    Release(moniker);
                    throw;
                }
                finally { Release(bagObject); }
            }

            var matches = devices.Where(device => string.Equals(device.Path, request.DeviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.Name, request.DeviceId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                return new(false, matches.Length == 0
                    ? "目标视频设备不存在，请连接设备并重新选择对应关系"
                    : "存在同名视频设备，无法唯一确认目标设备，请使用唯一设备标识");
            }
            return SupportsMode(matches[0].Moniker, request.Mode)
                ? new(true, "Windows 驱动已确认设备和视频模式")
                : new(false, "目标设备未声明支持存档的分辨率、帧率和像素格式");
        }
        finally
        {
            foreach (var device in devices) { Release(device.Moniker); }
            Release(enumerator);
            Release(enumeratorObject);
        }
    }

    private static bool SupportsMode(IMoniker moniker, VideoMode mode)
    {
        object? filterObject = null;
        IEnumPins? pins = null;
        try
        {
            var filterId = new Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770");
            moniker.BindToObject(null!, null!, ref filterId, out filterObject);
            Marshal.ThrowExceptionForHR(((IBaseFilter)filterObject).EnumPins(out pins));
            var pin = new object[1];
            while (pins.Next(1, pin, nint.Zero) == 0)
            {
                try
                {
                    if (pin[0] is not IAMStreamConfig config) { continue; }
                    Marshal.ThrowExceptionForHR(config.GetNumberOfCapabilities(out var count, out var size));
                    if (count is < 1 or > 10000 || size is < 1 or > 65536) { continue; }
                    var capabilities = Marshal.AllocCoTaskMem(size);
                    try
                    {
                        for (var index = 0; index < count; index++)
                        {
                            nint mediaPointer = nint.Zero;
                            try
                            {
                                Marshal.ThrowExceptionForHR(config.GetStreamCaps(index, out mediaPointer, capabilities));
                                if (mediaPointer == nint.Zero) { continue; }
                                var media = Marshal.PtrToStructure<MediaType>(mediaPointer);
                                if (MatchesMediaType(media, capabilities, size, mode)) { return true; }
                            }
                            finally
                            {
                                if (mediaPointer != nint.Zero)
                                {
                                    var media = Marshal.PtrToStructure<MediaType>(mediaPointer);
                                    if (media.Format != nint.Zero) { Marshal.FreeCoTaskMem(media.Format); }
                                    if (media.Unknown != nint.Zero) { Marshal.Release(media.Unknown); }
                                    Marshal.FreeCoTaskMem(mediaPointer);
                                }
                            }
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(capabilities); }
                }
                finally { Release(pin[0]); }
            }
            return false;
        }
        finally { Release(pins); Release(filterObject); }
    }

    private static bool MatchesMediaType(MediaType media, nint capabilities, int size, VideoMode mode)
    {
        var bitmapOffset = media.FormatType == new Guid("05589F80-C356-11CE-BF01-00AA0055595A") ? 48
            : media.FormatType == new Guid("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA") ? 72 : -1;
        if (bitmapOffset < 0 || media.Format == nint.Zero || media.FormatSize < bitmapOffset + 40
            || !MatchesDimensions(media, bitmapOffset, capabilities, size, mode)
            || !MatchesPixelFormat(media.Subtype, mode.PixelFormat)
            || mode.FramesPerSecondNumerator <= 0 || mode.FramesPerSecondDenominator <= 0)
        {
            return false;
        }
        var interval = 10_000_000L * mode.FramesPerSecondDenominator / mode.FramesPerSecondNumerator;
        var declaredInterval = Marshal.ReadInt64(media.Format, 40);
        if (interval == declaredInterval) { return true; }
        // VIDEO_STREAM_CONFIG_CAPS: MinFrameInterval/MaxFrameInterval are 100ns units.
        return size >= 128 && MatchesInterval(interval,
            Marshal.ReadInt64(capabilities, 104), Marshal.ReadInt64(capabilities, 112));
    }

    internal static bool MatchesInterval(long requested, long minimum, long maximum) =>
        requested > 0 && minimum > 0 && maximum >= minimum && requested >= minimum && requested <= maximum;

    private static bool MatchesDimensions(MediaType media, int bitmapOffset, nint capabilities, int size, VideoMode mode)
    {
        if (Marshal.ReadInt32(media.Format, bitmapOffset + 4) == mode.Width
            && Math.Abs((long)Marshal.ReadInt32(media.Format, bitmapOffset + 8)) == mode.Height)
        {
            return mode.Width > 0 && mode.Height > 0;
        }
        // Drivers may declare a supported range rather than enumerate each resolution.
        return size >= 128
            && MatchesDimension(mode.Width, Marshal.ReadInt32(capabilities, 60),
                Marshal.ReadInt32(capabilities, 68), Marshal.ReadInt32(capabilities, 76))
            && MatchesDimension(mode.Height, Marshal.ReadInt32(capabilities, 64),
                Marshal.ReadInt32(capabilities, 72), Marshal.ReadInt32(capabilities, 80));
    }

    internal static bool MatchesDimension(int requested, int minimum, int maximum, int granularity) =>
        minimum > 0 && maximum >= minimum && requested >= minimum && requested <= maximum
        && (minimum == maximum || granularity > 0 && (requested - minimum) % granularity == 0);

    internal static bool MatchesPixelFormat(Guid subtype, string pixelFormat)
    {
        // Numeric values are from the installed 12.9.2 MediaSDK VIDEO_PIXEL_FORMAT enum.
        var fourCc = pixelFormat switch
        {
            "1" => "I420",
            "2" => "YV12",
            "3" => "NV12",
            "4" => "NV21",
            "5" => "UYVY",
            "6" => "YUY2",
            "13" => "MJPG",
            "19" => "YVYU",
            "20" => "P010",
            "21" => "P016",
            _ => null
        };
        if (fourCc is not null)
        {
            var code = (uint)fourCc[0] | (uint)fourCc[1] << 8 | (uint)fourCc[2] << 16 | (uint)fourCc[3] << 24;
            return subtype == new Guid(unchecked((int)code), 0, 0x10, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71);
        }
        return false; // Unknown format is never guessed or treated as supported.
    }

    private static string ReadProperty(IPropertyBag bag, string name)
    {
        object value = string.Empty;
        return bag.Read(name, ref value, nint.Zero) == 0 ? value as string ?? string.Empty : string.Empty;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) { Marshal.ReleaseComObject(value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MediaType
    {
        public Guid Major;
        public Guid Subtype;
        [MarshalAs(UnmanagedType.Bool)] public bool FixedSize;
        [MarshalAs(UnmanagedType.Bool)] public bool TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public nint Unknown;
        public int FormatSize;
        public nint Format;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value, nint errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
        void GetClassId(out Guid id);
        void Stop();
        void Pause();
        void Run(long start);
        void GetState(int timeout, out int state);
        void SetSyncSource(nint clock);
        void GetSyncSource(out nint clock);
        [PreserveSig] int EnumPins(out IEnumPins pins);
    }

    [ComImport, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumPins
    {
        [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0, ArraySubType = UnmanagedType.IUnknown)] object[] pins, nint fetched);
    }

    [ComImport, Guid("C6E13340-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(nint mediaType);
        [PreserveSig] int GetFormat(out nint mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out nint mediaType, nint capabilities);
    }
}
