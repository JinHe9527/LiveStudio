using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveStudio.Desktop.ViewModels;

namespace LiveStudio.Desktop.Views;

public partial class CameraProfilesView : UserControl
{
    public CameraProfilesView()
    {
        InitializeComponent();
    }

    private void SaveCameraStationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not Button { Tag: CameraStationEditorViewModel station })
        {
            return;
        }

        viewModel.SaveCameraStationCommand.Execute(station);
    }

    private void DeleteCameraStationClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not Button { Tag: CameraStationEditorViewModel station })
        {
            return;
        }

        viewModel.DeleteCameraStationCommand.Execute(station);
    }
}
