using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LiveStudio.Desktop.ViewModels;
using System.Text.Json;

namespace LiveStudio.Desktop.Views;

public partial class SettingsView : UserControl
{
    private static readonly JsonSerializerOptions CompatibilityReportJsonOptions = new() { WriteIndented = true };
    private async void ExportCompatibilityReportClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel { TargetCompatibility: { } report } viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storageProvider)
        {
            return;
        }
        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出本机兼容报告",
                SuggestedFileName = "LiveStudio-compatibility.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("兼容报告") { Patterns = ["*.json"] }],
                ShowOverwritePrompt = true
            });
            if (file is null) { return; }
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, report, CompatibilityReportJsonOptions);
            viewModel.SettingsMessage = "兼容报告已导出，不含参数原值。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.SettingsMessage = $"兼容报告导出失败：{exception.Message}";
        }
    }

    private static readonly FilePickerFileType NativeExportFileType = new("直播伴侣原生导出包")
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"]
    };

    public SettingsView()
    {
        InitializeComponent();
    }

    private async void ClearLocalSnapshotsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (this.FindAncestorOfType<SnapshotsView>() is { } snapshotsView)
        {
            await snapshotsView.DeleteAllSnapshotsFromTitleBarAsync();
        }
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
