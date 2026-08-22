using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace LiveStudio.Agent;

public sealed class TrayIconService(
    IHostApplicationLifetime applicationLifetime,
    ILogger<TrayIconService> logger) : IHostedService, IDisposable
{
    private static readonly Action<ILogger, Exception?> LogDesktopLaunchFailure = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1301, nameof(LogDesktopLaunchFailure)),
        "无法启动 LiveStudio 桌面端");
    private readonly ManualResetEventSlim initialized = new(false);
    private readonly ManualResetEventSlim stopped = new(false);
    private Thread? trayThread;
    private Forms.Control? dispatcher;
    private Forms.ApplicationContext? context;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        trayThread = new Thread(RunTray)
        {
            IsBackground = true,
            Name = "LiveStudio Agent Tray"
        };
        trayThread.SetApartmentState(ApartmentState.STA);
        trayThread.Start();
        if (!initialized.Wait(TimeSpan.FromSeconds(5), cancellationToken))
        {
            throw new TimeoutException("LiveStudio Agent 系统托盘启动超时");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var currentDispatcher = dispatcher;
        var currentContext = context;
        if (currentDispatcher is { IsHandleCreated: true } && currentContext is not null)
        {
            currentDispatcher.BeginInvoke(currentContext.ExitThread);
        }

        await Task.Run(() => stopped.Wait(cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        initialized.Dispose();
        stopped.Dispose();
    }

    private void RunTray()
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        using var icon = LoadApplicationIcon();
        using var menu = new Forms.ContextMenuStrip();
        using var notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "LiveStudio Agent 正在运行",
            Visible = true,
            ContextMenuStrip = menu
        };
        var openItem = menu.Items.Add("打开 LiveStudio");
        openItem.Click += (_, _) => OpenDesktop();
        menu.Items.Add(new Forms.ToolStripSeparator());
        var statusItem = menu.Items.Add("执行端正在后台运行");
        statusItem.Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exitItem = menu.Items.Add("退出 Agent");
        exitItem.Click += (_, _) => applicationLifetime.StopApplication();
        notifyIcon.DoubleClick += (_, _) => OpenDesktop();

        dispatcher = new Forms.Control();
        dispatcher.CreateControl();
        context = new Forms.ApplicationContext();
        initialized.Set();
        try
        {
            Forms.Application.Run(context);
        }
        finally
        {
            notifyIcon.Visible = false;
            context.Dispose();
            dispatcher.Dispose();
            context = null;
            dispatcher = null;
            stopped.Set();
        }
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executablePath)
            && Icon.ExtractAssociatedIcon(executablePath) is { } icon
            ? icon
            : (Icon)SystemIcons.Application.Clone();
    }

    private void OpenDesktop()
    {
        try
        {
            var executablePath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "Desktop", "LiveStudio.Desktop.exe"));
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("安装目录中缺少 Desktop\\LiveStudio.Desktop.exe", executablePath);
            }

            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            LogDesktopLaunchFailure(logger, exception);
        }
    }
}
