using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveStudio.Core;
using Microsoft.Win32;

namespace LiveStudio.Adapters.Obs;

public sealed record ObsProcessInfo(int ProcessId, string ExecutablePath);

public static class ObsProcessController
{
    private const uint WindowMessageClose = 0x0010;
    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    public static ObsProcessInfo? FindRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = Process.GetProcessesByName("obs64").FirstOrDefault();
        if (process is null)
        {
            return null;
        }

        try
        {
            return new ObsProcessInfo(process.Id, process.MainModule?.FileName ?? string.Empty);
        }
        catch
        {
            return new ObsProcessInfo(process.Id, string.Empty);
        }
    }

    public static string FindExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("OBS 进程生命周期管理仅支持 Windows");
        }

        if (FindRunning() is { ExecutablePath.Length: > 0 } running)
        {
            return running.ExecutablePath;
        }

        using var appPath = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe");
        if (appPath?.GetValue(null) is string registered && File.Exists(registered))
        {
            return registered;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var candidate = Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("没有找到 OBS Studio，请先安装 OBS 或启动一次 OBS");
    }

    public static async Task<ObsProcessInfo> StartAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        var process = Process.Start(CreateStartInfo(executable))
            ?? throw new InvalidOperationException("无法启动 OBS Studio");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.HasExited)
            {
                return new ObsProcessInfo(process.Id, executable);
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("OBS Studio 启动失败");
    }

    internal static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(executable),
        Arguments = "--minimize-to-tray --disable-shutdown-check"
    };

    public static async Task StopAsync(int processId, CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            return;
        }

        _ = process.CloseMainWindow();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return;
            }

            _ = TryRequestNormalClose(processId);
            await Task.Delay(250, cancellationToken);
        }

        await WindowsProcessTerminator.TerminateAsync(
            processId,
            process.ProcessName,
            "OBS Studio",
            cancellationToken);
    }

    internal static bool TryDismissUncleanShutdownDialog(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var dismissed = false;
        _ = EnumWindows((windowHandle, ignored) =>
        {
            _ = ignored;
            _ = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
            if (ownerProcessId != processId)
            {
                return true;
            }

            var length = GetWindowTextLength(windowHandle);
            if (length <= 0)
            {
                return true;
            }

            var title = new char[length + 1];
            var written = GetWindowText(windowHandle, title, title.Length);
            if (written <= 0 || !IsUncleanShutdownDialogTitle(new string(title, 0, written)))
            {
                return true;
            }

            dismissed = PostMessage(windowHandle, WindowMessageClose, nuint.Zero, nint.Zero);
            return !dismissed;
        }, nint.Zero);
        return dismissed;
    }

    internal static bool TryRequestNormalClose(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var requested = false;
        _ = EnumWindows((windowHandle, ignored) =>
        {
            _ = ignored;
            _ = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
            if (ownerProcessId == processId)
            {
                requested |= PostMessage(windowHandle, WindowMessageClose, nuint.Zero, nint.Zero);
            }

            return true;
        }, nint.Zero);
        return requested;
    }

    internal static bool IsUncleanShutdownDialogTitle(string title) =>
        title.Contains("OBS Studio", StringComparison.OrdinalIgnoreCase)
        && (title.Contains("Crash Detected", StringComparison.OrdinalIgnoreCase)
            || title.Contains("崩溃", StringComparison.Ordinal)
            || title.Contains("當機", StringComparison.Ordinal));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, [Out] char[] text, int maximumLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);
}
