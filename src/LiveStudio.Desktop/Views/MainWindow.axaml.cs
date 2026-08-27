using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;

namespace LiveStudio.Desktop.Views;

public partial class MainWindow : Window
{
    private const int WindowStyleIndex = -16;
    private const long MaximizeBoxStyle = 0x00010000L;
    private const uint FrameChangedFlags = 0x0027;

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindowOpened;
        PropertyChanged += MainWindowPropertyChanged;
    }

    private void MainWindowOpened(object? sender, EventArgs eventArgs)
    {
        WindowState = WindowState.Normal;
        DisableWindowsMaximizeBox();
    }

    private void MainWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == WindowStateProperty
            && WindowState == WindowState.Maximized)
        {
            Dispatcher.UIThread.Post(() => WindowState = WindowState.Normal);
        }
    }

    private void DisableWindowsMaximizeBox()
    {
        if (!OperatingSystem.IsWindows()
            || TryGetPlatformHandle()?.Handle is not { } handle
            || handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, WindowStyleIndex);
        _ = SetWindowLongPtr(handle, WindowStyleIndex, new IntPtr(style.ToInt64() & ~MaximizeBoxStyle));
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, FrameChangedFlags);
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
