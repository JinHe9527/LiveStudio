using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveStudio.Adapters.LiveCompanion;

/// <summary>
/// 直播伴侣 12.8.1 没有公开的来源创建 API。删除来源后，仅恢复 WBStore 会被
/// MediaSDK 的原生来源清单再次清除，因此签名适配器必须调用该版本自带的添加摄像头
/// 与效果包导入界面。坐标全部相对目标窗口，且每一步都以预期窗口形态作为保护条件。
/// </summary>
internal static class LiveCompanionNativeUiRestorer
{
    private static readonly (double Horizontal, double Vertical)[] CameraEntryPoints =
    [
        // 1280×800 及此前真机循环使用的竖屏布局。
        (0.4375, 0.565),
        // 1080×720 紧凑布局中“摄像头”卡片的中心；首次 Chromium 点击丢失时重试。
        (0.426, 0.605),
        (0.4375, 0.565)
    ];

    private const uint WindowMessageMouseMove = 0x0200;
    private const uint WindowMessageLeftButtonDown = 0x0201;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const nuint MouseKeyLeftButton = 0x0001;
    private const int ShowRestore = 9;

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    public static async Task AddCameraAndImportEffectAsync(
        int processId,
        string effectPackagePath,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        var addResult = await OpenAddCameraOrSettingsAsync(processId, cancellationToken);
        if (addResult.Settings is not null)
        {
            await ImportEffectAsync(
                processId,
                addResult.Settings,
                effectPackagePath,
                cancellationToken);
            return;
        }

        // camera-payloads.json 已在停机阶段按设备名称写入；当前签名版本会预选唯一
        // 匹配设备。若设备不存在，后续不会出现设置窗口，事务会失败并回滚。
        await ClickAsync(addResult.AddCamera!.Handle, 0.862, 0.960, cancellationToken);
        var settings = await WaitForWindowAsync(
            processId,
            IsCameraSettingsWindow,
            TimeSpan.FromSeconds(20),
            "摄像头设置窗口",
            cancellationToken);
        await ImportEffectAsync(processId, settings, effectPackagePath, cancellationToken);
    }

    private static async Task<AddCameraWindowResult> OpenAddCameraOrSettingsAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        foreach (var entryPoint in CameraEntryPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var main = await WaitForMainWindowAsync(processId, cancellationToken);
            var alreadyOpen = FindAddCameraOrSettings(processId, main.Handle);
            if (alreadyOpen is not null)
            {
                return alreadyOpen;
            }

            // Electron 首次渲染或窗口刚从后台恢复时可能接收 hover 却丢失 click。
            // 每轮重新解析真实主窗口，且只在预期摄像头窗口未出现时重试。
            await ClickAsync(
                main.Handle,
                entryPoint.Horizontal,
                entryPoint.Vertical,
                cancellationToken);
            var opened = await TryWaitForAddCameraOrSettingsAsync(
                processId,
                main.Handle,
                TimeSpan.FromSeconds(6),
                cancellationToken);
            if (opened is not null)
            {
                return opened;
            }

            await Task.Delay(750, cancellationToken);
        }

        throw new InvalidOperationException(
            "未出现预期的添加摄像头或摄像头设置窗口；已重新定位直播伴侣窗口并重试，恢复已取消");
    }

    public static async Task ImportEffectForExistingCameraAsync(
        int processId,
        string effectPackagePath,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        var existingSettings = EnumerateWindows(processId)
            .Where(IsCameraSettingsWindow)
            .OrderByDescending(window => window.Rectangle.Area)
            .FirstOrDefault();
        if (existingSettings is not null)
        {
            await ImportEffectAsync(processId, existingSettings, effectPackagePath, cancellationToken);
            return;
        }

        var main = await WaitForMainWindowAsync(processId, cancellationToken);

        // 单摄像头签名路径：来源行尾更多菜单 -> 设置。
        await ClickAsync(main.Handle, 0.208, 0.272, cancellationToken);
        await Task.Delay(450, cancellationToken);
        await ClickAsync(main.Handle, 0.144, 0.322, cancellationToken);
        var settings = await WaitForWindowAsync(
            processId,
            IsCameraSettingsWindow,
            TimeSpan.FromSeconds(15),
            "摄像头设置窗口",
            cancellationToken);
        await ImportEffectAsync(processId, settings, effectPackagePath, cancellationToken);
    }

    private static async Task ImportEffectAsync(
        int processId,
        NativeWindow settings,
        string effectPackagePath,
        CancellationToken cancellationToken)
    {
        // Electron 顶层窗句柄会先于渲染内容就绪；过早点击会落在尚未挂载的空白层。
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        // 左侧“导入导出”与右侧“导入配置文件”。Electron 偶尔会在窗口句柄出现后
        // 继续重建该页面；以文件窗口作为唯一成功条件进行有界重试，不能把一次消息
        // 发送成功误认为按钮已经执行。
        NativeWindow? dialog = null;
        for (var attempt = 1; attempt <= 3 && dialog is null; attempt++)
        {
            await ClickAsync(settings.Handle, 0.070, 0.503, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await ClickAsync(settings.Handle, 0.853, 0.190, cancellationToken);
            dialog = await TryWaitForWindowAsync(
                processId,
                IsFileDialog,
                TimeSpan.FromSeconds(6),
                cancellationToken);
        }

        if (dialog is null)
        {
            throw new InvalidOperationException("未出现预期的原生配置文件选择窗口，恢复已取消");
        }

        SelectFile(dialog.Handle, effectPackagePath);

        await WaitForWindowToCloseAsync(
            dialog.Handle,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        await Task.Delay(700, cancellationToken);

        // “导入配置文件后，将覆盖已有摄像头设置。是否继续？”
        await ClickAsync(settings.Handle, 0.595, 0.586, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        await ClickAsync(settings.Handle, 0.970, 0.030, cancellationToken);
        await Task.Delay(500, cancellationToken);
    }

    public static async Task DeleteSingleCameraAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        var main = await WaitForMainWindowAsync(processId, cancellationToken);
        // Chromium 主窗口句柄出现后，来源行还会继续挂载。等待真实渲染内容稳定，
        // 避免连续破坏恢复时把点击发到尚未生成的来源菜单。
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await ClickAsync(main.Handle, 0.208, 0.272, cancellationToken);
        await Task.Delay(450, cancellationToken);
        await ClickAsync(main.Handle, 0.153, 0.736, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private static void SelectFile(nint dialogHandle, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("直播伴侣原生效果包不存在", fullPath);
        }

        if (!WindowsAutomationBridge.SelectFile(dialogHandle, fullPath))
        {
            throw new InvalidOperationException("直播伴侣原生文件窗口无法定位目标效果包");
        }
    }

    private static async Task ClickAsync(
        nint windowHandle,
        double horizontalRatio,
        double verticalRatio,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindow(windowHandle)
            || !GetClientRect(windowHandle, out var rectangle)
            || rectangle.Width <= 0
            || rectangle.Height <= 0)
        {
            throw new InvalidOperationException("直播伴侣原生恢复窗口已关闭");
        }

        _ = ShowWindow(windowHandle, ShowRestore);
        var inputTarget = FindDescendantByClass(windowHandle, "Chrome_RenderWidgetHostHWND");
        if (inputTarget == nint.Zero)
        {
            inputTarget = windowHandle;
        }

        var inputPoint = new NativePoint
        {
            X = (int)Math.Round(rectangle.Width * horizontalRatio),
            Y = (int)Math.Round(rectangle.Height * verticalRatio)
        };
        if (inputTarget != windowHandle
            && (!ClientToScreen(windowHandle, ref inputPoint)
                || !ScreenToClient(inputTarget, ref inputPoint)))
        {
            throw new InvalidOperationException("无法定位直播伴侣原生恢复控件");
        }

        var messagePoint = new nint((inputPoint.Y << 16) | (inputPoint.X & 0xffff));
        if (!PostMessage(inputTarget, WindowMessageMouseMove, nuint.Zero, messagePoint))
        {
            throw new InvalidOperationException("无法向直播伴侣原生恢复窗口发送控件操作");
        }

        // 来源行的更多按钮只在 hover 后挂载；必须先让 Chromium 完成一次渲染。
        await Task.Delay(200, cancellationToken);
        if (!PostMessage(inputTarget, WindowMessageLeftButtonDown, MouseKeyLeftButton, messagePoint)
            || !PostMessage(inputTarget, WindowMessageLeftButtonUp, nuint.Zero, messagePoint))
        {
            throw new InvalidOperationException("无法向直播伴侣原生恢复窗口发送控件操作");
        }

        await Task.Delay(250, cancellationToken);
    }

    private static async Task<NativeWindow> WaitForWindowAsync(
        int processId,
        Func<NativeWindow, bool> predicate,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        return await TryWaitForWindowAsync(processId, predicate, timeout, cancellationToken)
               ?? throw new InvalidOperationException($"未出现预期的{description}，恢复已取消");
    }

    private static async Task<NativeWindow?> TryWaitForWindowAsync(
        int processId,
        Func<NativeWindow, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = EnumerateWindows(processId)
                .Where(predicate)
                .OrderByDescending(window => window.Rectangle.Area)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    private static async Task<NativeWindow> WaitForMainWindowAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var running = LiveCompanionProcessController.FindRunning();
                var mainHandle = nint.Zero;
                if (running is not null)
                {
                    using var process = Process.GetProcessById(running.ProcessId);
                    mainHandle = process.MainWindowHandle;
                }

                var candidates = EnumerateWindows(processId)
                    .Where(IsMainWindow)
                    .ToArray();
                // 此版本会创建一个同尺寸的黑色占位顶层窗。已有摄像头时，真实窗口
                // 具有原生预览 View_ 子窗，必须优先选择它；否则再使用进程主句柄。
                var match = candidates.FirstOrDefault(window =>
                                HasDescendantClassPrefix(window.Handle, "View_"))
                            ?? candidates.FirstOrDefault(window => window.Handle == mainHandle);
                if (match is not null)
                {
                    return match;
                }
            }
            catch (ArgumentException)
            {
                // 启动器可能在 Chromium 窗口就绪前替换主进程；下一轮重新解析 PID。
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("未出现预期的直播伴侣主窗口，恢复已取消");
    }

    private static async Task<AddCameraWindowResult?> TryWaitForAddCameraOrSettingsAsync(
        int processId,
        nint mainHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = FindAddCameraOrSettings(processId, mainHandle);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    private static AddCameraWindowResult? FindAddCameraOrSettings(
        int processId,
        nint mainHandle)
    {
        var windows = EnumerateWindows(processId);
        var settings = windows.FirstOrDefault(IsCameraSettingsWindow);
        if (settings is not null)
        {
            return new AddCameraWindowResult(null, settings);
        }

        var addCamera = windows.FirstOrDefault(window =>
            window.Handle != mainHandle && IsAddCameraWindow(window));
        return addCamera is null ? null : new AddCameraWindowResult(addCamera, null);
    }

    private static async Task WaitForWindowToCloseAsync(
        nint windowHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(windowHandle) || !IsWindowVisible(windowHandle))
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new InvalidOperationException("直播伴侣没有接受原生效果包文件");
    }

    private static List<NativeWindow> EnumerateWindows(int processId)
    {
        var result = new List<NativeWindow>();
        var processIds = new HashSet<int> { processId };
        if (LiveCompanionProcessController.FindRunning() is { } running)
        {
            processIds.Add(running.ProcessId);
        }

        foreach (var processName in new[]
                 {
                     "StreamingTool", "douyin-live-companion", "douyin_live_companion",
                     "LiveCompanion", "直播伴侣"
                 })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    processIds.Add(process.Id);
                }
            }
        }

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)
                || GetWindowThreadProcessId(handle, out var ownerProcessId) == 0
                || !processIds.Contains(ownerProcessId)
                || !GetWindowRect(handle, out var rectangle)
                || rectangle.Width < 200
                || rectangle.Height < 100)
            {
                return true;
            }

            result.Add(new NativeWindow(
                handle,
                GetClassNameValue(handle),
                GetWindowTextValue(handle),
                rectangle));
            return true;
        }, nint.Zero);
        return result;
    }

    private static nint FindDescendantByClass(nint parentWindow, string className)
    {
        nint result = nint.Zero;
        EnumChildWindows(parentWindow, (handle, _) =>
        {
            if (IsWindowVisible(handle)
                && string.Equals(GetClassNameValue(handle), className, StringComparison.Ordinal))
            {
                result = handle;
                return false;
            }

            return true;
        }, nint.Zero);
        return result;
    }

    private static bool HasDescendantClassPrefix(nint parentWindow, string classNamePrefix)
    {
        var found = false;
        EnumChildWindows(parentWindow, (handle, _) =>
        {
            if (GetClassNameValue(handle).StartsWith(classNamePrefix, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, nint.Zero);
        return found;
    }

    private static bool IsMainWindow(NativeWindow window) =>
        string.Equals(window.ClassName, "Chrome_WidgetWin_1", StringComparison.Ordinal)
        && window.Rectangle.Width >= 1000
        && window.Rectangle.Height >= 650
        && window.Rectangle.Width / (double)window.Rectangle.Height >= 1.55;

    private static bool IsAddCameraWindow(NativeWindow window) => MatchesAddCameraWindow(
        window.ClassName,
        window.Title,
        window.Rectangle.Width,
        window.Rectangle.Height);

    private static bool IsCameraSettingsWindow(NativeWindow window) => MatchesCameraSettingsWindow(
        window.ClassName,
        window.Title,
        window.Rectangle.Width,
        window.Rectangle.Height);

    internal static bool MatchesAddCameraWindow(
        string className,
        string title,
        int width,
        int height) =>
        string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal)
        && (title.Contains("摄像头", StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(title)
                && width >= 850
                && height is >= 450 and <= 620
                && width / (double)height >= 1.45));

    internal static bool MatchesCameraSettingsWindow(
        string className,
        string title,
        int width,
        int height) =>
        string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal)
        && (title.Contains("摄像头设置", StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(title)
                && width is >= 560 and <= 820
                && height is >= 560 and <= 800
                && width / (double)height is >= 0.80 and <= 1.25));

    private static bool IsFileDialog(NativeWindow window) =>
        string.Equals(window.ClassName, "#32770", StringComparison.Ordinal)
        && window.Rectangle.Width >= 600
        && window.Rectangle.Height >= 350;

    private static unsafe string GetClassNameValue(nint windowHandle)
    {
        var value = stackalloc char[256];
        var length = GetClassName(windowHandle, value, 256);
        return length > 0 ? new string(value, 0, length) : string.Empty;
    }

    private static unsafe string GetWindowTextValue(nint windowHandle)
    {
        var value = stackalloc char[512];
        var length = GetWindowText(windowHandle, value, 512);
        return length > 0 ? new string(value, 0, length) : string.Empty;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("直播伴侣原生恢复仅支持 Windows");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetClassName(nint windowHandle, char* className, int maximumLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetWindowText(nint windowHandle, char* text, int maximumLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public long Area => (long)Width * Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed record NativeWindow(
        nint Handle,
        string ClassName,
        string Title,
        NativeRectangle Rectangle);

    private sealed record AddCameraWindowResult(
        NativeWindow? AddCamera,
        NativeWindow? Settings);
}
