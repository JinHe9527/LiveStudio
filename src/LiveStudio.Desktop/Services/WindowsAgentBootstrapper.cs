using System.Diagnostics;

namespace LiveStudio.Desktop.Services;

internal static class WindowsAgentBootstrapper
{
    public static bool EnsureRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var existing = Process.GetProcessesByName("LiveStudio.Agent").FirstOrDefault();
        if (existing is not null)
        {
            return false;
        }

        var desktopDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var packageRoot = Directory.GetParent(desktopDirectory)?.FullName;
        if (packageRoot is null)
        {
            return false;
        }

        var agentPath = Path.Combine(packageRoot, "Agent", "LiveStudio.Agent.exe");
        if (!File.Exists(agentPath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(agentPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(agentPath)
        });
        return true;
    }
}
