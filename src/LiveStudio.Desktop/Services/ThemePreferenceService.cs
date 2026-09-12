using Avalonia;
using Avalonia.Styling;

namespace LiveStudio.Desktop.Services;

internal static class ThemePreferenceService
{
    internal const string SystemMode = "system";
    internal const string LightMode = "light";
    internal const string DarkMode = "dark";

    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "theme-mode.txt");

    internal static string LoadMode()
    {
        try
        {
            var value = File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim() : DarkMode;
            return value is SystemMode or LightMode or DarkMode ? value : DarkMode;
        }
        catch (IOException)
        {
            return DarkMode;
        }
        catch (UnauthorizedAccessException)
        {
            return DarkMode;
        }
    }

    internal static void ApplySavedMode() => Apply(LoadMode(), false);

    internal static void Apply(string mode, bool persist = true)
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = mode switch
            {
                LightMode => ThemeVariant.Light,
                DarkMode => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        if (!persist)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, mode is LightMode or DarkMode ? mode : SystemMode);
        }
        catch (IOException)
        {
            // 主题切换已即时生效；无法保存时下次启动使用已有偏好或默认深色。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上，权限问题不应阻断界面使用。
        }
    }
}
