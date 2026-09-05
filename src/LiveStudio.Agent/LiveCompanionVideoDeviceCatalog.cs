using System.Diagnostics;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Agent;

public sealed class LiveCompanionVideoDeviceCatalog : ILiveCompanionVideoDeviceCatalog
{
    public async Task<RestorePreflightResult> ValidateAsync(string deviceId, VideoMode mode, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "LiveStudio.Agent.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("probe-video-device");
        start.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new VideoDeviceProbeRequest(deviceId, mode))));
        using var process = Process.Start(start) ?? throw new IOException("无法启动视频设备检查进程");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = JsonSerializer.Deserialize<VideoDeviceProbeResult>(await output);
            _ = await errors;
            return process.ExitCode == 0 && result?.IsSupported == true
                ? RestorePreflightResult.Success
                : RestorePreflightResult.Fail(JobStatus.MappingRequired, result?.Message ?? "视频设备检查未返回有效结果");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RestorePreflightResult.Fail(JobStatus.MappingRequired, "视频设备驱动检查超时，尚未写入任何画面配置");
        }
        catch (JsonException)
        {
            return RestorePreflightResult.Fail(JobStatus.MappingRequired, "视频设备驱动检查失败，尚未写入任何画面配置");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
