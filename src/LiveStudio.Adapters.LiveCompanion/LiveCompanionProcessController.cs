using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionProcessController
{
    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    private static readonly string[] ProcessNames =
    [
        "StreamingTool", "douyin-live-companion", "douyin_live_companion", "LiveCompanion", "直播伴侣"
    ];

    public static LiveCompanionProcessInfo? FindRunning()
    {
        LiveCompanionProcessInfo? fallback = null;
        foreach (var processName in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var executablePath = process.MainModule?.FileName;
                        var version = ResolveVersion(
                            executablePath,
                            process.MainModule?.FileVersionInfo.ProductVersion,
                            process.MainModule?.FileVersionInfo.FileVersion);
                        var candidate = new LiveCompanionProcessInfo(process.Id, executablePath, version);
                        if (process.MainWindowHandle != nint.Zero)
                        {
                            return candidate;
                        }

                        fallback ??= candidate;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        fallback ??= new LiveCompanionProcessInfo(process.Id, null, "unknown");
                    }
                }
            }
        }

        return fallback;
    }

    internal static string ResolveVersion(
        string? executablePath,
        string? productVersion,
        string? fileVersion)
    {
        var versionDirectory = executablePath is null
            ? null
            : Directory.GetParent(executablePath)?.Name;
        if (versionDirectory is not null
            && Version.TryParse(versionDirectory, out _))
        {
            return versionDirectory;
        }

        return productVersion ?? fileVersion ?? "unknown";
    }

    internal static string ResolveInstalledVersion(string executablePath)
    {
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        return ResolveVersion(
            executablePath,
            versionInfo.ProductVersion,
            versionInfo.FileVersion);
    }

    public static string? FindInstalledExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                continue;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                var displayName = entry?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)
                    || !IsLiveCompanionDisplayName(displayName))
                {
                    continue;
                }

                var displayIcon = NormalizeExecutablePath(entry?.GetValue("DisplayIcon") as string);
                if (displayIcon is not null)
                {
                    return displayIcon;
                }

                if (entry?.GetValue("InstallLocation") is not string installLocation
                    || string.IsNullOrWhiteSpace(installLocation))
                {
                    continue;
                }

                foreach (var processName in ProcessNames)
                {
                    var candidate = Path.Combine(installLocation, $"{processName}.exe");
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return FindVersionedInstallation();
    }

    private static string? FindVersionedInstallation()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, "webcast_mate"));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "webcast_mate"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "webcast_mate"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "webcast_mate"));

        var candidates = new List<(Version Version, string Path)>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (!Version.TryParse(Path.GetFileName(directory), out var version))
                    {
                        continue;
                    }

                    foreach (var executableName in new[]
                             {
                                 "直播伴侣.exe",
                                 "StreamingTool.exe",
                                 "douyin-live-companion.exe"
                             })
                    {
                        var candidate = Path.Combine(directory, executableName);
                        if (File.Exists(candidate))
                        {
                            candidates.Add((version, Path.GetFullPath(candidate)));
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static bool IsLiveCompanionDisplayName(string value) =>
        value.Contains("抖音直播伴侣", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Douyin Live Companion", StringComparison.OrdinalIgnoreCase)
        || value.Contains("TikTok LIVE Studio", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        if (path.StartsWith('"'))
        {
            var closingQuote = path.IndexOf('"', 1);
            path = closingQuote > 1 ? path[1..closingQuote] : path.Trim('"');
        }
        else
        {
            var argumentSeparator = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (argumentSeparator >= 0)
            {
                path = path[..(argumentSeparator + 4)];
            }
        }

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    public static async Task StopAsync(int processId, CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            return;
        }

        var executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("无法确认直播伴侣进程路径，未修改任何配置");
        }

        RequestNormalClose(executablePath);
        if (await WaitUntilStoppedAsync(executablePath, TimeSpan.FromSeconds(8), cancellationToken))
        {
            return;
        }

        ForceTerminate(executablePath);
        if (!await WaitUntilStoppedAsync(executablePath, TimeSpan.FromSeconds(12), cancellationToken))
        {
            throw new InvalidOperationException("直播伴侣仍有同版本配置进程未退出，未修改任何配置");
        }
    }

    private static void RequestNormalClose(string executablePath)
    {
        foreach (var process in FindProcesses(executablePath))
        {
            using (process)
            {
                if (!process.HasExited && process.MainWindowHandle != nint.Zero)
                {
                    _ = process.CloseMainWindow();
                }
            }
        }
    }

    private static void ForceTerminate(string executablePath)
    {
        foreach (var process in FindProcesses(executablePath))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // 进程在枚举和终止之间已经退出。
                }
            }
        }
    }

    private static async Task<bool> WaitUntilStoppedAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = FindProcesses(executablePath);
            if (processes.Count == 0)
            {
                return true;
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static List<Process> FindProcesses(string executablePath)
    {
        var result = new List<Process>();
        foreach (var processName in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (MatchesExecutablePath(process.MainModule?.FileName, executablePath))
                    {
                        result.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    internal static bool MatchesExecutablePath(string? candidatePath, string expectedPath) =>
        !string.IsNullOrWhiteSpace(candidatePath)
        && string.Equals(
            Path.GetFullPath(candidatePath),
            Path.GetFullPath(expectedPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

public sealed record LiveCompanionProcessInfo(int ProcessId, string? ExecutablePath, string Version);
