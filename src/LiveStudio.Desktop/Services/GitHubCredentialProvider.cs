using System.ComponentModel;
using System.Diagnostics;

namespace LiveStudio.Desktop.Services;

internal static class GitHubCredentialProvider
{
    public static async Task<string?> TryGetTokenAsync(CancellationToken cancellationToken)
    {
        foreach (var executable in FindExecutables())
        {
            var token = await TryReadTokenAsync(executable, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static IEnumerable<string> FindExecutables()
    {
        yield return "gh";

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "GitHub CLI", "gh.exe");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        yield return Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe");
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "gh.exe");

        var packagesDirectory = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packagesDirectory))
        {
            yield break;
        }

        IEnumerable<string> packageDirectories;
        try
        {
            packageDirectories = Directory.EnumerateDirectories(packagesDirectory, "GitHub.cli_*").ToArray();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var packageDirectory in packageDirectories)
        {
            string? executable = null;
            try
            {
                executable = Directory.EnumerateFiles(packageDirectory, "gh.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (executable is not null)
            {
                yield return executable;
            }
        }
    }

    private static async Task<string?> TryReadTokenAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathFullyQualified(executable) && !File.Exists(executable))
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("auth");
            process.StartInfo.ArgumentList.Add("token");
            process.StartInfo.ArgumentList.Add("--hostname");
            process.StartInfo.ArgumentList.Add("github.com");

            if (!process.Start())
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception
            or IOException
            or InvalidOperationException)
        {
            return null;
        }
    }
}
