using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Desktop.Views;

public partial class SettingsView : UserControl
{
    private static readonly FilePickerFileType NativeExportFileType = new("直播伴侣原生导出包")
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"]
    };

    public SettingsView()
    {
        InitializeComponent();
    }

    private void BasicSettingsTabClicked(object? sender, RoutedEventArgs eventArgs)
    {
        BasicSettingsTab.IsChecked = true;
        ActivitySettingsTab.IsChecked = false;
    }

    private void ActivitySettingsTabClicked(object? sender, RoutedEventArgs eventArgs)
    {
        BasicSettingsTab.IsChecked = false;
        ActivitySettingsTab.IsChecked = true;
    }

    private async void ChooseLanDirectoryClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storageProvider)
        {
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择局域网存档目录",
            AllowMultiple = false
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ConfigureLanDirectoryAsync(path);
        }
    }

    private async void ChooseNativeExportBaselineClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var path = await ChooseNativeExportAsync("选择只修改美颜参数之前的 ZIP");
        if (path is not null && DataContext is MainViewModel viewModel)
        {
            await viewModel.SetNativeExportBaselineAsync(path);
        }
    }

    private async void ChooseNativeExportAfterClicked(object? sender, RoutedEventArgs eventArgs)
    {
        var path = await ChooseNativeExportAsync("选择只修改一个美颜参数之后的 ZIP");
        if (path is not null && DataContext is MainViewModel viewModel)
        {
            await viewModel.CompareNativeExportAsync(path);
        }
    }

    private async Task<string?> ChooseNativeExportAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return null;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [NativeExportFileType]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
