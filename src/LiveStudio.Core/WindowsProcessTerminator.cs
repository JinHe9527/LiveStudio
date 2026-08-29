using System.ComponentModel;
using System.Diagnostics;

namespace LiveStudio.Core;

public static class WindowsProcessTerminator
{
    private const int AccessDeniedError = 5;
    private const int OperationCancelledError = 1223;

    public static async Task TerminateAsync(
        int processId,
        string expectedProcessName,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("进程权限处理仅支持 Windows");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProcessName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        using var process = TryGetExpectedProcess(processId, expectedProcessName, displayName);
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            return;
        }
        catch (InvalidOperationException)
        {
            // The process exited between discovery and termination.
            return;
        }
        catch (Exception exception) when (RequiresElevation(exception))
        {
            // Continue through the signed Windows taskkill executable. This is only reached
            // when the target application is running at a higher integrity level.
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var current = TryGetExpectedProcess(processId, expectedProcessName, displayName);
        if (current is null)
        {
            return;
        }

        var taskKillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        if (!File.Exists(taskKillPath))
        {
            throw new InvalidOperationException($"Windows 系统终止工具不存在，无法关闭 {displayName}");
        }

        Process? elevatedProcess;
        try
        {
            elevatedProcess = Process.Start(CreateElevatedStartInfo(processId));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == OperationCancelledError)
        {
            throw new InvalidOperationException(
                $"恢复需要管理员权限关闭 {displayName}；管理员授权已取消，配置没有写入。",
                exception);
        }
        catch (Exception exception) when (RequiresElevation(exception))
        {
            throw new InvalidOperationException(
                $"Windows 拒绝关闭 {displayName}。请在恢复时弹出的管理员授权窗口中选择“是”。",
                exception);
        }

        if (elevatedProcess is null)
        {
            throw new InvalidOperationException($"无法启动 Windows 系统终止工具来关闭 {displayName}");
        }

        using (elevatedProcess)
        {
            await elevatedProcess.WaitForExitAsync(cancellationToken);
            if (elevatedProcess.ExitCode != 0
                && TryGetExpectedProcess(processId, expectedProcessName, displayName) is { } remaining)
            {
                remaining.Dispose();
                throw new InvalidOperationException(
                    $"Windows 未能关闭 {displayName}（系统返回 {elevatedProcess.ExitCode}），配置没有写入。");
            }
        }
    }

    public static bool RequiresElevation(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        Win32Exception win32Exception when win32Exception.NativeErrorCode == AccessDeniedError => true,
        _ when exception.InnerException is not null => RequiresElevation(exception.InnerException),
        _ => false
    };

    internal static ProcessStartInfo CreateElevatedStartInfo(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"))
        {
            Arguments = $"/PID {processId} /T /F",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static Process? TryGetExpectedProcess(
        int processId,
        string expectedProcessName,
        string displayName)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (!string.Equals(
                    process.ProcessName,
                    expectedProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var actualProcessName = process.ProcessName;
                process.Dispose();
                throw new InvalidOperationException(
                    $"拒绝关闭 {displayName}：进程标识已变更（{expectedProcessName} -> {actualProcessName}）");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
