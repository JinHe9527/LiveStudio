using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LiveStudio.Agent;

public static class WindowsStartupRegistration
{
    private const string StartupTaskId = "LiveStudioAgentStartup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiveStudio Agent";
    private const int NoPackage = 15700;
    private const int InsufficientBuffer = 122;

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged())
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (IsPackaged())
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                task.Disable();
                return;
            }

            var state = task.State == StartupTaskState.Disabled
                ? await task.RequestEnableAsync()
                : task.State;
            if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
            {
                throw new InvalidOperationException("Windows 已阻止此启动项，请在任务管理器的“启动应用”中启用 LiveStudio Agent");
            }

            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("无法确定 LiveStudio Agent 程序路径");
        }

        key.SetValue(ValueName, $"\"{Path.GetFullPath(executablePath)}\"", RegistryValueKind.String);
    }

    private static bool IsPackaged()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result switch
        {
            NoPackage => false,
            InsufficientBuffer or 0 => true,
            _ => throw new Win32Exception(result, "无法读取当前 Windows 应用包标识")
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
