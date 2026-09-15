using System;
using Avalonia;

namespace LiveStudio.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!args.Contains("--demo") && Environment.GetEnvironmentVariable("DOTNET_WATCH") != "1"
            && Environment.GetEnvironmentVariable("LIVESTUDIO_LOCAL_ONLY") != "1")
            LiveStudio.Diagnostics.ErrorDiagnostics.Start("Desktop");
        var instanceName = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1"
            ? "LiveStudio.Desktop.Development.SingleInstance"
            : "LiveStudio.Desktop.SingleInstance";
        using var instanceMutex = new Mutex(true, instanceName, out var isFirstInstance);
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
