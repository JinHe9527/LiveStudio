using System;
using Avalonia;

namespace LiveStudio.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instanceMutex = new Mutex(true, "LiveStudio.Desktop.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
