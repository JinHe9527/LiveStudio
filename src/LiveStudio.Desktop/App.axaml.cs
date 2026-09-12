using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LiveStudio.Desktop.ViewModels;
using LiveStudio.Desktop.Views;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemePreferenceService.ApplySavedMode();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var isDemoMode = desktop.Args?.Contains("--demo", StringComparer.OrdinalIgnoreCase) == true;
            var isDevelopment = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";
            var viewModel = new MainViewModel(isDemoMode);
            viewModel.UpdateRestartRequested += (_, _) => desktop.Shutdown();
            var window = new MainWindow
            {
                DataContext = viewModel,
                Title = isDevelopment ? "LiveStudio · 开发模式（保存代码后自动刷新）" : "LiveStudio",
            };
            window.Opened += async (_, _) =>
            {
                // 开发窗口复用正在运行的执行端，避免替换已安装版本的 Agent。
                if (!isDevelopment && WindowsAgentBootstrapper.EnsureRunning())
                {
                    await Task.Delay(800);
                }

                await viewModel.InitializeAsync();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
