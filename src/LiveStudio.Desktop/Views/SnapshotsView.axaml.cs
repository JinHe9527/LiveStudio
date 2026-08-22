using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Desktop.Views;

public partial class SnapshotsView : UserControl
{
    private static readonly FilePickerFileType SnapshotFileType = new("LiveStudio 画面存档")
    {
        Patterns = ["*.lscfg"],
        MimeTypes = ["application/vnd.livestudio.snapshot"],
        AppleUniformTypeIdentifiers = ["public.data"]
    };

    public SnapshotsView()
    {
        InitializeComponent();
    }

    private async void ImportSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开画面存档",
            AllowMultiple = false,
            FileTypeFilter = [SnapshotFileType],
            SuggestedFileType = SnapshotFileType
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ImportSnapshotFileAsync(path);
        }
    }

    private async void ExportSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel { SelectedSnapshot: { } selectedSnapshot } viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storageProvider)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出画面存档",
            SuggestedFileName = $"{selectedSnapshot.Id:N}.lscfg",
            DefaultExtension = "lscfg",
            FileTypeChoices = [SnapshotFileType],
            SuggestedFileType = SnapshotFileType,
            ShowOverwritePrompt = true
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ExportSelectedSnapshotAsync(path);
        }
    }
}
