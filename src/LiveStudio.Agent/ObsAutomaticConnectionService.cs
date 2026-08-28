using System.Net.WebSockets;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class ObsAutomaticConnectionService(AgentObsConfigurationStore configurationStore)
{
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var companionOriginal = LiveCompanionProcessController.FindRunning();
        LiveCompanionProcessInfo? companionStarted = null;
        try
        {
            if (companionOriginal is null)
            {
                var executablePath = LiveCompanionProcessController.FindInstalledExecutable()
                    ?? throw new InvalidOperationException(
                        "没有从 Windows 安装信息找到抖音直播伴侣，请先启动一次直播伴侣后重试");
                await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
                await LiveCompanionProcessController.WaitUntilRunningAsync(executablePath, cancellationToken);
                companionStarted = LiveCompanionProcessController.FindRunning()
                    ?? throw new InvalidOperationException("抖音直播伴侣已启动，但进程识别失败");
            }

            await ConnectObsAsync(cancellationToken);
        }
        catch
        {
            if (companionStarted is not null)
            {
                await LiveCompanionProcessController.StopAsync(
                    companionStarted.ProcessId,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task ConnectObsAsync(CancellationToken cancellationToken)
    {
        var configurationPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio",
            "plugin_config",
            "obs-websocket",
            "config.json");
        var configurationFile = new ObsWebSocketConfigurationFile(configurationPath);
        var configuration = configurationFile.Read();
        var running = ObsProcessController.FindRunning();
        ObsWebSocketConfigurationTransaction? transaction = null;
        ObsProcessInfo? started = null;

        if (!configuration.ServerEnabled)
        {
            if (running is not null)
            {
                if (!await ObsUiAutomationConnector.TryEnableServerAsync(
                        running.ProcessId,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        "无法自动打开 OBS WebSocket 设置，请确认 OBS 主窗口未被其他对话框遮挡后重试");
                }

                configuration = configurationFile.Read();
                if (!configuration.ServerEnabled)
                {
                    throw new InvalidOperationException("OBS 未确认 WebSocket 设置，未修改 LiveStudio 凭据");
                }
            }
            else
            {
                transaction = configurationFile.EnableAuthenticated();
                configuration = transaction.Configuration;
            }
        }

        var endpoint = new Uri($"ws://127.0.0.1:{configuration.Port}");
        var password = configuration.AuthenticationRequired ? configuration.Password : string.Empty;
        try
        {
            if (running is null)
            {
                started = await ObsProcessController.StartAsync(cancellationToken);
            }

            await WaitUntilConnectedAsync(endpoint, password, cancellationToken);
            await configurationStore.SaveAsync(
                new ConfigureObsRequest(endpoint, password),
                cancellationToken);
            transaction?.Commit();
        }
        catch
        {
            if (started is not null)
            {
                await ObsProcessController.StopAsync(started.ProcessId, CancellationToken.None);
            }

            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static async Task WaitUntilConnectedAsync(
        Uri endpoint,
        string password,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new ObsWebSocketClient(endpoint, password);
                await client.ConnectAsync(cancellationToken);
                _ = await client.CallAsync("GetStreamStatus", null, cancellationToken);
                _ = await client.CallAsync("GetRecordStatus", null, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or ObsRequestException)
            {
                lastException = exception;
                await Task.Delay(500, cancellationToken);
            }
        }

        throw new InvalidOperationException("OBS 已启动，但 WebSocket 连接验证未通过", lastException);
    }
}
