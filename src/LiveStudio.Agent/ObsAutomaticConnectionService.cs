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
        var running = ObsProcessController.FindRunning();
        ObsWebSocketConfigurationTransaction? transaction = null;
        ObsProcessInfo? started = null;

        try
        {
            if (!File.Exists(configurationPath))
            {
                if (running is null)
                {
                    started = await ObsProcessController.StartAsync(cancellationToken);
                    running = started;
                }

                for (var attempt = 0; attempt < 40 && !File.Exists(configurationPath); attempt++)
                {
                    await Task.Delay(250, cancellationToken);
                }
            }

            var configuration = configurationFile.Read();
            if (!configuration.ServerEnabled)
            {
                if (running is not null)
                {
                    if (!await ObsUiAutomationConnector.TryEnableServerAsync(
                            running.ProcessId,
                            cancellationToken))
                    {
                        throw new InvalidOperationException(
                            "无法自动开启 OBS WebSocket。请处理 OBS 提示框，并确认两款软件运行权限一致；"
                            + "也可在 OBS 的“工具 → WebSocket 服务器设置”中开启后，使用下方手动连接。");
                    }

                    configuration = configurationFile.Read();
                    if (!configuration.ServerEnabled)
                    {
                        throw new InvalidOperationException("OBS 未确认 WebSocket 设置，请在 OBS 中应用设置后重试；未修改 LiveStudio 凭据");
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
        await ObsAdapter.WaitUntilConnectedAsync(
            async token =>
            {
                await using var client = new ObsWebSocketClient(endpoint, password);
                await client.ConnectAsync(token);
                _ = await client.CallAsync("GetVersion", null, token);
            },
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }
}
