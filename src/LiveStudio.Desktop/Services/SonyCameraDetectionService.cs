using System.Diagnostics;
using System.Text.Json;

namespace LiveStudio.Desktop.Services;

public sealed record SonyCameraDevice(string Name, string InstanceId);

public interface ISonyCameraDetectionService
{
    Task<IReadOnlyList<SonyCameraDevice>> DetectAsync(CancellationToken cancellationToken);
}

public sealed class SonyCameraDetectionService : ISonyCameraDetectionService
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(8);

    private const string DetectionScript =
        "$ErrorActionPreference='Stop'; "
        + "@(Get-PnpDevice -PresentOnly -InstanceId 'USB\\VID_054C*' -ErrorAction SilentlyContinue | "
        + "Select-Object @{n='name';e={if($_.FriendlyName){$_.FriendlyName}else{'Sony 相机'}}},"
        + "@{n='instanceId';e={$_.InstanceId}}) | ConvertTo-Json -Compress";

    public async Task<IReadOnlyList<SonyCameraDevice>> DetectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(DetectionScript);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Windows 相机检测");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DetectionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
                // The process exited while the timeout handler was running.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            throw new InvalidOperationException("Sony USB 相机检测超时，请重新连接 USB 数据线后再试");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "Windows 相机检测失败" : error.Trim());
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        using var document = JsonDocument.Parse(output);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return elements
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => new SonyCameraDevice(
                ReadString(element, "name") ?? "Sony 相机",
                ReadString(element, "instanceId") ?? string.Empty))
            .Where(device => !string.IsNullOrWhiteSpace(device.InstanceId) && LooksLikeCamera(device.Name))
            .DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool LooksLikeCamera(string name) =>
        name.Contains("Camera", StringComparison.OrdinalIgnoreCase)
        || name.Contains("相机", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ILME", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ILCE", StringComparison.OrdinalIgnoreCase)
        || name.Contains("FX3", StringComparison.OrdinalIgnoreCase)
        || name.Contains("FX30", StringComparison.OrdinalIgnoreCase)
        || name.Contains("7SM3", StringComparison.OrdinalIgnoreCase)
        || name.Contains("A7S", StringComparison.OrdinalIgnoreCase);
}
