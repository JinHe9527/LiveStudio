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
            LiveStudio.Diagnostics.ErrorDiagnostics.Record(exception);
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
        AutomaticErrorReporting.IsVisible = LiveStudio.Diagnostics.ErrorDiagnostics.HasSubmissionEndpoint;
        CheckDiagnostics.IsVisible = LiveStudio.Diagnostics.ErrorDiagnostics.HasSubmissionEndpoint;
        AutomaticErrorReporting.IsChecked = LiveStudio.Diagnostics.ErrorDiagnostics.Enabled;
        DiagnosticsStatus.Text = LiveStudio.Diagnostics.ErrorDiagnostics.Status;
    }

    private async void ExportDiagnosticPackageClicked(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storageProvider)
        {
            DiagnosticsStatus.Text = "当前窗口无法选择保存位置，请重新打开设置后重试。";
            return;
        }
        ExportDiagnosticPackage.IsEnabled = false;
        try
        {
            var compatibility = (DataContext as MainViewModel)?.TargetCompatibility;
            var safeCompatibility = compatibility is null ? null : new LiveStudio.Diagnostics.DiagnosticCompatibilitySummary(
                compatibility.CheckedAt, compatibility.ApplicationVersion, compatibility.StructureFingerprint,
                compatibility.Status, compatibility.FieldCount, compatibility.Differences.Count);
            DiagnosticsStatus.Text = "正在整理脱敏诊断信息…";
            var package = await Task.Run(() => LiveStudio.Diagnostics.ErrorDiagnostics.CreatePackage(safeCompatibility));
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出诊断包",
                SuggestedFileName = package.FileName,
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("故障诊断包") { Patterns = ["*.zip"] }],
                ShowOverwritePrompt = true
            });
            if (file is null)
            {
                DiagnosticsStatus.Text = "已取消导出；本机错误记录仍保留。";
                return;
            }
            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                await stream.WriteAsync(package.Bytes);
                await stream.FlushAsync();
            }
            DiagnosticsStatus.Text = $"已导出诊断包，包含 {package.ErrorKinds} 类错误。请将 ZIP 发给开发者，并说明操作步骤和发生时间。"
                + (package.SkippedFiles > 0 ? $"另有 {package.SkippedFiles} 份记录无法读取或已跳过，详见包内说明。" : "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or System.Text.Json.JsonException)
        {
            LiveStudio.Diagnostics.ErrorDiagnostics.Record(exception);
            DiagnosticsStatus.Text = "诊断包导出失败。请确认保存位置可写并重试；本机原有错误记录仍保留。";
        }
        finally { ExportDiagnosticPackage.IsEnabled = true; }
    }

    private void AutomaticErrorReportingChanged(object? sender, RoutedEventArgs args)
    {
        LiveStudio.Diagnostics.ErrorDiagnostics.Enabled = AutomaticErrorReporting.IsChecked == true;
        if (DiagnosticsStatus is not null) DiagnosticsStatus.Text = LiveStudio.Diagnostics.ErrorDiagnostics.Status;
    }

    private async void CheckDiagnosticsClicked(object? sender, RoutedEventArgs args)
    {
        await LiveStudio.Diagnostics.ErrorDiagnostics.FlushAsync();
        DiagnosticsStatus.Text = LiveStudio.Diagnostics.ErrorDiagnostics.Status;
    }

    private void OpenDiagnosticsClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(LiveStudio.Diagnostics.ErrorDiagnostics.DirectoryPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                LiveStudio.Diagnostics.ErrorDiagnostics.DirectoryPath)
            { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            LiveStudio.Diagnostics.ErrorDiagnostics.Record(exception);
            DiagnosticsStatus.Text = "无法打开错误记录目录。";
        }
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
