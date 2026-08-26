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

        var agentPath = FindAgentPath();
        if (agentPath is null)
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(agentPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(agentPath)
        });
        return true;
    }

    private static string? FindAgentPath()
    {
        var desktopDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var packageRoot = Directory.GetParent(desktopDirectory)?.FullName;
        if (packageRoot is not null)
        {
            var packagedAgent = Path.Combine(packageRoot, "Agent", "LiveStudio.Agent.exe");
            if (File.Exists(packagedAgent))
            {
                return packagedAgent;
            }
        }

        var configurationDirectory = Directory.GetParent(desktopDirectory);
        var desktopProjectDirectory = configurationDirectory?.Parent?.Parent;
        var sourceDirectory = desktopProjectDirectory?.Parent;
        if (configurationDirectory is null || sourceDirectory is null)
        {
            return null;
        }

        var agentBuildDirectory = Path.Combine(
            sourceDirectory.FullName,
            "LiveStudio.Agent",
            "bin",
            configurationDirectory.Name);
        if (!Directory.Exists(agentBuildDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(
                    agentBuildDirectory,
                    "LiveStudio.Agent.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
