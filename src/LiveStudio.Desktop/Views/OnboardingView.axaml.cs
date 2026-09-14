using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LiveStudio.Desktop.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("SkipGuide")?.Focus());
    }
}
