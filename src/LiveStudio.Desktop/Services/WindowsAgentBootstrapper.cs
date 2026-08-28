using System.Diagnostics;
using System.ComponentModel;

namespace LiveStudio.Desktop.Services;

internal static class WindowsAgentBootstrapper
{
    public static bool EnsureRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var agentPath = FindAgentPath();
        if (agentPath is null)
        {
            return false;
        }

        var expectedAgentRunning = false;
        foreach (var existing in Process.GetProcessesByName("LiveStudio.Agent"))
        {
            using (existing)
            {
                var existingPath = TryGetExecutablePath(existing);
                if (PathsEqual(existingPath, agentPath))
                {
                    expectedAgentRunning = true;
                    continue;
                }

                if (existingPath is null)
                {
                    continue;
                }

                try
                {
                    existing.Kill();
                    existing.WaitForExit(5_000);
                }
                catch (InvalidOperationException)
                {
                    // 进程已在检查与结束之间退出，可以继续启动当前包内 Agent。
                }
                catch (Win32Exception)
                {
                    return false;
                }
            }
        }

        if (expectedAgentRunning)
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

    internal static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
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
