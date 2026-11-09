using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus.Shell.Views;

/// <summary>
/// Wraps a mode's compact pill and adds the "+N" chip when three or more things are active
/// (Figma: Queue · More waiting). One frame per mode, so switching modes still crossfades.
/// </summary>
public sealed class CompactFrame : StackPanel
{
    private readonly Border _chip;
    private readonly TextBlock _chipText;

    public CompactFrame(FrameworkElement compactView)
    {
        Orientation = Orientation.Horizontal;
        Children.Add(new ContentPresenter { Content = compactView });

        _chipText = new TextBlock();
        _chipText.SetResourceReference(StyleProperty, "Text.Caption");
        _chipText.Foreground = (Brush)Application.Current.FindResource("Brush.TextPrimary");

        _chip = new Border
        {
            Child = _chipText,
            Margin = new Thickness(-4, 0, 12, 0),
            Visibility = Visibility.Collapsed,
        };
        _chip.SetResourceReference(StyleProperty, "Chip");
        Children.Add(_chip);
    }

    public int Overflow
    {
        set
        {
            _chip.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
            _chipText.Text = $"+{value}";
        }
    }
}
