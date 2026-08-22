using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LiveStudio.Adapters.LiveCompanion;

internal sealed class LiveCompanionProcessController
{
    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    private static readonly string[] ProcessNames =
    [
        "StreamingTool", "douyin-live-companion", "douyin_live_companion", "LiveCompanion", "直播伴侣"
    ];

    public static LiveCompanionProcessInfo? FindRunning()
    {
        foreach (var processName in ProcessNames)
        {
            using var process = Process.GetProcessesByName(processName).FirstOrDefault();
            if (process is null)
            {
                continue;
            }

            try
            {
                var executablePath = process.MainModule?.FileName;
                var version = process.MainModule?.FileVersionInfo.ProductVersion
                    ?? process.MainModule?.FileVersionInfo.FileVersion
                    ?? "unknown";
                return new LiveCompanionProcessInfo(process.Id, executablePath, version);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new LiveCompanionProcessInfo(process.Id, null, "unknown");
            }
        }

        return null;
    }

    public static async Task StopAsync(int processId, CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            return;
        }

        if (!process.CloseMainWindow())
        {
            throw new InvalidOperationException("直播伴侣不接受正常关闭请求");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("直播伴侣关闭超时，未修改任何配置", exception);
        }
    }

    public static Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("无法确定直播伴侣安装目录");
        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        });
        return Task.CompletedTask;
    }

    public static async Task WaitUntilRunningAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindRunning() is not null)
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("直播伴侣启动超时");
    }

    public static bool TryInspectWindowLiveState(int processId, out bool isLive)
    {
        if (!OperatingSystem.IsWindows())
        {
            isLive = false;
            return false;
        }

        var texts = new List<string>();
        EnumWindows((windowHandle, _) =>
        {
            var threadId = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
            if (threadId == 0 || ownerProcessId != processId)
            {
                return true;
            }

            AddWindowText(windowHandle, texts);
            EnumChildWindows(windowHandle, (childHandle, _) =>
            {
                AddWindowText(childHandle, texts);
                return true;
            }, nint.Zero);
            return true;
        }, nint.Zero);

        if (texts.Any(text => text.Contains("结束直播", StringComparison.Ordinal)
                              || text.Contains("停止直播", StringComparison.Ordinal)
                              || text.Contains("直播中", StringComparison.Ordinal)
                              || text.Contains("正在直播", StringComparison.Ordinal)))
        {
            isLive = true;
            return true;
        }

        if (texts.Any(text => text.Contains("开始直播", StringComparison.Ordinal)
                              || text.Contains("立即开播", StringComparison.Ordinal)
                              || text.Contains("开始推流", StringComparison.Ordinal)
                              || text.Contains("发起直播", StringComparison.Ordinal)))
        {
            isLive = false;
            return true;
        }

        isLive = false;
        return false;
    }

    [SupportedOSPlatform("windows")]
    public static byte[]? CaptureWindowPng(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var windowHandle = process.MainWindowHandle;
        if (windowHandle == nint.Zero
            || IsIconic(windowHandle)
            || !GetWindowRect(windowHandle, out var rectangle))
        {
            return null;
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                rectangle.Left,
                rectangle.Top,
                0,
                0,
                new Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static void AddWindowText(nint windowHandle, List<string> destination)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return;
        }

        var buffer = new char[length + 1];
        var written = GetWindowText(windowHandle, buffer, buffer.Length);
        if (written > 0)
        {
            destination.Add(new string(buffer, 0, written));
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, [Out] char[] text, int maximumLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record LiveCompanionProcessInfo(int ProcessId, string? ExecutablePath, string Version);
