using System.Diagnostics;
using LiveStudio.Core;
using Microsoft.Win32;

namespace LiveStudio.Adapters.Obs;

public sealed record ObsProcessInfo(int ProcessId, string ExecutablePath);

public static class ObsProcessController
{
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
        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable),
            Arguments = "--minimize-to-tray"
        }) ?? throw new InvalidOperationException("无法启动 OBS Studio");
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

    public static async Task StopAsync(int processId, CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
            return;
        }

        _ = process.CloseMainWindow();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (TimeoutException)
        {
            await WindowsProcessTerminator.TerminateAsync(
                processId,
                process.ProcessName,
                "OBS Studio",
                cancellationToken);
        }
        catch (Exception exception) when (WindowsProcessTerminator.RequiresElevation(exception))
        {
            await WindowsProcessTerminator.TerminateAsync(
                processId,
                process.ProcessName,
                "OBS Studio",
                cancellationToken);
        }
    }
}
