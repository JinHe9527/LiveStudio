using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LiveStudio.Desktop.ViewModels;
using LiveStudio.Packaging;

namespace LiveStudio.Desktop.Views;

public partial class SnapshotsView : UserControl
{
    private static readonly FilePickerFileType SnapshotFileType = new("LiveStudio 画面存档")
    {
        Patterns = ["*.lscfg"],
        MimeTypes = ["application/vnd.livestudio.snapshot"],
        AppleUniformTypeIdentifiers = ["public.data"]
    };

    private static readonly FilePickerFileType CameraReferenceImageFileType = new("相机画面截图")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg"],
        MimeTypes = ["image/png", "image/jpeg"],
        AppleUniformTypeIdentifiers = ["public.png", "public.jpeg"]
    };

    public SnapshotsView()
    {
        InitializeComponent();
    }

    private void SnapshotsViewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (!eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
            || DataContext is not MainViewModel { SnapshotInspector: { } inspector })
        {
            return;
        }

        if (eventArgs.Key == Key.F && inspector.SelectedApplication is { } application)
        {
            application.ShowSearch();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.I)
        {
            inspector.IsTechnicalPanelOpen = true;
            eventArgs.Handled = true;
        }
    }

    private void TechnicalInfoClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel { SnapshotInspector: { } inspector })
        {
            inspector.IsTechnicalPanelOpen = true;
        }
    }

    private async void CameraReferenceImageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: CameraStationEditorViewModel station }
            || DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        if (!viewModel.CanSaveCameraStationsToSelectedSnapshot)
        {
            SetCameraImageUnavailableMessage(viewModel);
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择{station.Name}参考画面",
            AllowMultiple = false,
            FileTypeFilter = [CameraReferenceImageFileType],
            SuggestedFileType = CameraReferenceImageFileType
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await StageCameraReferenceImageAsync(viewModel, station, path);
        }
    }

    private void CameraReferenceImageDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = DataContext is MainViewModel { CanSaveCameraStationsToSelectedSnapshot: true }
            && eventArgs.DataTransfer.TryGetFiles() is { Length: > 0 }
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void CameraReferenceImageDropped(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (sender is not Border { Tag: CameraStationEditorViewModel station }
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.CanSaveCameraStationsToSelectedSnapshot)
        {
            SetCameraImageUnavailableMessage(viewModel);
            return;
        }

        var path = eventArgs.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!string.IsNullOrWhiteSpace(path))
        {
            await StageCameraReferenceImageAsync(viewModel, station, path);
        }
    }

    private static async Task StageCameraReferenceImageAsync(
        MainViewModel viewModel,
        CameraStationEditorViewModel station,
        string path)
    {
        try
        {
            var image = await CameraReferenceImageFile.ReadAsync(path, CancellationToken.None);
            station.StageReferenceImage(path, image);
            if (viewModel.SnapshotInspector is { } inspector)
            {
                inspector.CameraSaveMessage = $"已选择{station.Name}参考图；点“保存三个机位”写入当前存档";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            if (viewModel.SnapshotInspector is { } inspector)
            {
                inspector.CameraSaveMessage = exception.Message;
            }
        }
    }

    private void RemoveCameraReferenceImageClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: CameraStationEditorViewModel station }
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.CanSaveCameraStationsToSelectedSnapshot)
        {
            SetCameraImageUnavailableMessage(viewModel);
            return;
        }

        station.RemoveReferenceImage();
        if (viewModel.SnapshotInspector is { } inspector)
        {
            inspector.CameraSaveMessage = $"已移除{station.Name}参考图；点“保存三个机位”后生效";
        }
    }

    private static void SetCameraImageUnavailableMessage(MainViewModel viewModel)
    {
        if (viewModel.SnapshotInspector is { } inspector)
        {
            inspector.CameraSaveMessage = viewModel.SelectedSnapshot?.IsCloud == true
                ? "云端存档不能直接改写；请先保存当前画面，再给新的本机存档添加参考图"
                : "导入预览不能改写原文件；请先导入为本机存档";
        }
    }

    private async void ImportSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await ChooseSnapshotToImportAsync(applyAfterImport: false);
    }

    private async void ImportAndApplySnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        await ChooseSnapshotToImportAsync(applyAfterImport: true);
    }

    private async Task ChooseSnapshotToImportAsync(bool applyAfterImport)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = applyAfterImport ? "导入并应用画面存档" : "导入画面存档",
            AllowMultiple = false,
            FileTypeFilter = [SnapshotFileType],
            SuggestedFileType = SnapshotFileType
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ImportSnapshotFileAsync(path, applyAfterImport);
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

    private async void RenameCurrentSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel { SelectedSnapshot: { } snapshot } viewModel)
        {
            await RenameSnapshotAsync(viewModel, snapshot);
        }
    }

    private async void RenameTimelineSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel
            && sender is MenuItem { Tag: LocalSnapshotItemViewModel snapshot })
        {
            viewModel.SelectedSnapshot = snapshot;
            await RenameSnapshotAsync(viewModel, snapshot);
        }
    }

    private async Task RenameSnapshotAsync(
        MainViewModel viewModel,
        LocalSnapshotItemViewModel snapshot)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (snapshot.IsDesktopFile)
        {
            viewModel.PendingImportMessage = "当前文件不是受管存档，不能在此修改名称";
            return;
        }

        var name = await ShowTextInputAsync(
            owner,
            "重命名存档",
            "只修改管理界面中的显示名称，不改动已签名的存档内容。",
            snapshot.DisplayName,
            "保存名称",
            120);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await viewModel.RenameSnapshotAsync(snapshot, name);
        }
    }

    private async void DeleteTimelineSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not MenuItem { Tag: LocalSnapshotItemViewModel snapshot }
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (snapshot.IsDesktopFile)
        {
            viewModel.PendingImportMessage = "当前文件不是受管存档，不能在此删除";
            return;
        }

        viewModel.SelectedSnapshot = snapshot;
        var confirmed = await ShowDeleteConfirmationAsync(
            owner,
            "删除存档",
            snapshot.IsCloud
                ? $"将永久删除云存档“{snapshot.DisplayName}”、云端预览和不再被引用的素材。此操作无法撤销，不会修改任何直播电脑。"
                : $"将永久删除“{snapshot.DisplayName}”及其本机 .lscfg 文件。此操作无法撤销，不会修改 OBS 或直播伴侣。",
            "删除存档");
        if (confirmed)
        {
            await viewModel.DeleteSelectedSnapshotAsync();
        }
    }

    private async void CreateRoomClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (!viewModel.IsCloudConnected)
        {
            viewModel.OpenCloudSettingsCommand.Execute(null);
            return;
        }

        var name = await ShowTextInputAsync(
            owner,
            "新建直播间",
            "直播间用于归档一台或一组直播电脑的画面存档。",
            string.Empty,
            "新建",
            100);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await viewModel.CreateCloudRoomAsync(name);
        }
    }

    private void CloudSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OpenCloudSettingsCommand.Execute(null);
        }
    }

    private async void DeleteCurrentSnapshotClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner
            || viewModel.SelectedSnapshot is not { } selectedSnapshot)
        {
            return;
        }

        var confirmed = await ShowDeleteConfirmationAsync(
            owner,
            "删除当前存档",
            selectedSnapshot.IsCloud
                ? $"将永久删除云存档“{selectedSnapshot.DisplayName}”、云端预览和不再被引用的素材。此操作无法撤销，不会修改任何直播电脑。"
                : $"将永久删除“{selectedSnapshot.DisplayName}”及其本机 .lscfg 文件。此操作无法撤销，不会修改 OBS 或直播伴侣。",
            "删除当前存档");
        if (confirmed)
        {
            await viewModel.DeleteSelectedSnapshotAsync();
        }
    }

    private async void DeleteAllSnapshotsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner
            || viewModel.LocalSnapshotCount == 0)
        {
            return;
        }

        var snapshotCount = viewModel.LocalSnapshotCount;
        var confirmed = await ShowDeleteConfirmationAsync(
            owner,
            "清空本机存档",
            $"将永久删除本机全部 {snapshotCount} 份画面存档及其 .lscfg 文件。此操作无法撤销，不会修改 OBS 或直播伴侣。",
            $"永久清空 {snapshotCount} 份");
        if (confirmed)
        {
            await viewModel.DeleteAllSnapshotsAsync();
        }
    }

    private static Task<bool> ShowDeleteConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant,
            Background = owner.Background
        };
        var cancel = new Button { Content = "取消", MinWidth = 76 };
        cancel.Classes.Add("archive-secondary");
        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 116
        };
        confirm.Classes.Add("archive-danger");
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    private static Task<string?> ShowTextInputAsync(
        Window owner,
        string title,
        string message,
        string initialValue,
        string confirmText,
        int maximumLength)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant,
            Background = owner.Background
        };
        var input = new TextBox
        {
            Text = initialValue,
            MaxLength = maximumLength,
            MinWidth = 360,
            PlaceholderText = title
        };
        input.Classes.Add("settings-input");
        var cancel = new Button { Content = "取消", MinWidth = 76 };
        cancel.Classes.Add("archive-secondary");
        var confirm = new Button { Content = confirmText, MinWidth = 96 };
        confirm.Classes.Add("archive-primary");
        cancel.Click += (_, _) => dialog.Close(null);
        confirm.Click += (_, _) => dialog.Close(input.Text?.Trim());
        input.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter && !string.IsNullOrWhiteSpace(input.Text))
            {
                dialog.Close(input.Text.Trim());
                eventArgs.Handled = true;
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowDialog<string?>(owner);
    }
}
