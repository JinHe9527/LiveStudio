using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LiveStudio.Discovery.Windows;

public sealed class DiscoveryCollector
{
    public static async Task<DiscoveryReport> CaptureAsync(
        string name,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> registryKeys,
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken)
    {
        var files = new List<FileObservation>();
        foreach (var root in roots)
        {
            files.AddRange(await CaptureFilesAsync(root, cancellationToken));
        }

        var registry = OperatingSystem.IsWindows()
            ? CaptureRegistry(registryKeys)
            : [];
        return new DiscoveryReport(
            name,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            CaptureProcesses(processNames),
            files.OrderBy(file => file.Root, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            registry);
    }

    private static async Task<IReadOnlyList<FileObservation>> CaptureFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(fullRoot);
        }

        var observations = new List<FileObservation>();
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            observations.Add(new FileObservation(
                fullRoot,
                Path.GetRelativePath(fullRoot, path),
                info.Length,
                info.LastWriteTimeUtc,
                Convert.ToHexStringLower(hash)));
        }

        return observations;
    }

    private static List<ProcessObservation> CaptureProcesses(IReadOnlyList<string> names)
    {
        var requested = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observations = new List<ProcessObservation>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!requested.Contains(process.ProcessName))
                    {
                        continue;
                    }

                    var module = process.MainModule;
                    observations.Add(new ProcessObservation(
                        process.ProcessName,
                        process.Id,
                        module?.FileName,
                        module?.FileVersionInfo.ProductVersion,
                        process.StartTime.ToUniversalTime()));
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return observations;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryObservation[] CaptureRegistry(IReadOnlyList<string> keyPaths)
    {
        var observations = new List<RegistryObservation>();
        foreach (var path in keyPaths)
        {
            var separator = path.IndexOf('\\', StringComparison.Ordinal);
            var hiveName = separator < 0 ? path : path[..separator];
            var subKeyPath = separator < 0 ? string.Empty : path[(separator + 1)..];
            var hive = hiveName.ToUpperInvariant() switch
            {
                "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
                "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
                _ => throw new ArgumentException($"不支持的 Registry 根: {hiveName}")
            };

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var serialized = value switch
                {
                    null => [],
                    byte[] bytes => bytes,
                    string[] strings => Encoding.UTF8.GetBytes(string.Join('\0', strings)),
                    _ => Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                };
                observations.Add(new RegistryObservation(
                    path,
                    valueName,
                    key.GetValueKind(valueName).ToString(),
                    Convert.ToHexStringLower(SHA256.HashData(serialized))));
            }
        }

        return observations
            .OrderBy(value => value.KeyPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ValueName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
