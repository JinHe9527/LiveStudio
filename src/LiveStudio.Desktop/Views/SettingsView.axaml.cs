using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
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
}
