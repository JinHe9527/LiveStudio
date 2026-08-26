using Avalonia;
using Avalonia.Controls;

namespace LiveStudio.Desktop.Controls;

public sealed class AdaptiveUniformPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveUniformPanel, double>(nameof(MinItemWidth), 208d);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<AdaptiveUniformPanel, double>(nameof(ItemHeight), 30d);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<AdaptiveUniformPanel, int>(nameof(MaxColumns), 5);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public static int CalculateColumnCount(double availableWidth, double minItemWidth, int maxColumns, int itemCount)
    {
        if (itemCount <= 0)
        {
            return 1;
        }

        var safeMinimum = Math.Max(1d, minItemWidth);
        var safeMaximum = Math.Max(1, maxColumns);
        var widthColumns = double.IsFinite(availableWidth)
            ? Math.Max(1, (int)Math.Floor(availableWidth / safeMinimum))
            : safeMaximum;
        return Math.Min(itemCount, Math.Min(safeMaximum, widthColumns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = Children.Count;
        var columns = CalculateColumnCount(availableSize.Width, MinItemWidth, MaxColumns, itemCount);
        var desiredWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : Math.Min(itemCount, columns) * MinItemWidth;
        var cellWidth = columns == 0 ? desiredWidth : desiredWidth / columns;
        var height = Math.Max(1d, ItemHeight);
        foreach (var child in Children)
        {
            child.Measure(new Size(cellWidth, height));
        }

        var rows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);
        return new Size(desiredWidth, rows * height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemCount = Children.Count;
        var columns = CalculateColumnCount(finalSize.Width, MinItemWidth, MaxColumns, itemCount);
        var cellWidth = columns == 0 ? finalSize.Width : finalSize.Width / columns;
        var height = Math.Max(1d, ItemHeight);
        for (var index = 0; index < itemCount; index++)
        {
            var row = index / columns;
            var column = index % columns;
            Children[index].Arrange(new Rect(column * cellWidth, row * height, cellWidth, height));
        }

        var rows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);
        return new Size(finalSize.Width, rows * height);
    }
}
