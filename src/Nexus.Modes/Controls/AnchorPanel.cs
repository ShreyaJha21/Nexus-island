using System.Windows;
using System.Windows.Controls;

namespace Nexus.Modes.Controls;

/// <summary>
/// Lays children out at their natural size, centered horizontally and pinned to the top or
/// bottom, without ever constraining them. The island animates its outer size while content
/// keeps its final layout, so text never reflows mid-animation; the surface simply clips it.
/// </summary>
public sealed class AnchorPanel : Panel
{
    public static readonly DependencyProperty AnchorBottomProperty = DependencyProperty.Register(
        nameof(AnchorBottom), typeof(bool), typeof(AnchorPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public bool AnchorBottom
    {
        get => (bool)GetValue(AnchorBottomProperty);
        set => SetValue(AnchorBottomProperty, value);
    }

    public static readonly DependencyProperty AnchorLeftProperty = DependencyProperty.Register(
        nameof(AnchorLeft), typeof(bool), typeof(AnchorPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Pin children to the left edge instead of centering them (the island docked in the taskbar).</summary>
    public bool AnchorLeft
    {
        get => (bool)GetValue(AnchorLeftProperty);
        set => SetValue(AnchorLeftProperty, value);
    }

    /// <summary>Raised (after layout) when a child's natural size changes, e.g. a longer track title.</summary>
    public event EventHandler? ChildSizeChanged;

    private readonly Dictionary<UIElement, Size> _lastSizes = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        var changed = false;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (!_lastSizes.TryGetValue(child, out var last) || last != child.DesiredSize)
            {
                changed |= _lastSizes.ContainsKey(child);
                _lastSizes[child] = child.DesiredSize;
            }
        }

        foreach (var gone in _lastSizes.Keys.Where(k => !InternalChildren.Contains(k)).ToList())
        {
            _lastSizes.Remove(gone);
        }

        if (changed)
        {
            Dispatcher.BeginInvoke(() => ChildSizeChanged?.Invoke(this, EventArgs.Empty),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            var x = AnchorLeft ? 0 : (finalSize.Width - size.Width) / 2;
            var y = AnchorBottom ? finalSize.Height - size.Height : 0;
            child.Arrange(new Rect(new Point(Math.Round(x), Math.Round(y)), size));
        }

        return finalSize;
    }
}
