using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiveStudio.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ImportAndApplySnapshotClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.ImportSnapshotFromTitleBarAsync(applyAfterImport: true);

    private async void ImportSnapshotClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.ImportSnapshotFromTitleBarAsync(applyAfterImport: false);

    private async void ExportSnapshotClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.ExportSelectedSnapshotFromTitleBarAsync();

    private async void RenameCurrentSnapshotClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.RenameSelectedSnapshotFromTitleBarAsync();

    private void TechnicalInfoClicked(object? sender, RoutedEventArgs eventArgs) =>
        SnapshotsPage.OpenTechnicalInformation();

    private async void DeleteCurrentSnapshotClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.DeleteSelectedSnapshotFromTitleBarAsync();

    private async void DeleteAllSnapshotsClicked(object? sender, RoutedEventArgs eventArgs) =>
        await SnapshotsPage.DeleteAllSnapshotsFromTitleBarAsync();

}
