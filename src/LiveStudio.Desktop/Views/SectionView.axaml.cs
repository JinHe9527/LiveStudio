using Avalonia;
using Avalonia.Controls;

namespace LiveStudio.Desktop.Views;

public partial class SectionView : UserControl
{
    public static readonly StyledProperty<string> EyebrowProperty =
        AvaloniaProperty.Register<SectionView, string>(nameof(Eyebrow), string.Empty);

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SectionView, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SectionView, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<string> EmptyTitleProperty =
        AvaloniaProperty.Register<SectionView, string>(nameof(EmptyTitle), string.Empty);

    public static readonly StyledProperty<string> EmptyDescriptionProperty =
        AvaloniaProperty.Register<SectionView, string>(nameof(EmptyDescription), string.Empty);

    public SectionView()
    {
        InitializeComponent();
    }

    public string Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string EmptyTitle
    {
        get => GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string EmptyDescription
    {
        get => GetValue(EmptyDescriptionProperty);
        set => SetValue(EmptyDescriptionProperty, value);
    }
}
