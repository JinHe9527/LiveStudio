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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            viewModel.UpdateRestartRequested += (_, _) => desktop.Shutdown();
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            window.Opened += async (_, _) =>
            {
                if (WindowsAgentBootstrapper.EnsureRunning())
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
