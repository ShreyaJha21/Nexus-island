using System.Windows;

namespace Nexus.Modes.Controls;

/// <summary>
/// Marks parts of a view that handle pointer drags themselves (the shelf's items drag out into other
/// apps), so dragging there doesn't pick up and move the island.
/// </summary>
public static class IslandInput
{
    public static readonly DependencyProperty OwnsDragProperty = DependencyProperty.RegisterAttached(
        "OwnsDrag", typeof(bool), typeof(IslandInput), new PropertyMetadata(false));

    public static bool GetOwnsDrag(DependencyObject element) => (bool)element.GetValue(OwnsDragProperty);

    public static void SetOwnsDrag(DependencyObject element, bool value) => element.SetValue(OwnsDragProperty, value);
}

/// <summary>
/// Starts a drag-out once the pointer has moved a few pixels with the button held, so a plain click
/// still works as a click.
/// </summary>
public sealed class DragStarter
{
    private readonly UIElement _element;
    private readonly Action _start;
    private Point? _down;

    public DragStarter(UIElement element, Action start)
    {
        _element = element;
        _start = start;
        element.PreviewMouseLeftButtonDown += (_, e) => _down = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => _down = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (_down is not { } from || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                _down = null;
                return;
            }

            var now = e.GetPosition(element);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _down = null;
            e.Handled = true;
            _start();
        };
    }
}
