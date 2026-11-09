using System.Windows;
using System.Windows.Media;

namespace Nexus.Shell.Island;

/// <summary>
/// A soft drop shadow drawn as a few stacked rounded rectangles with fading alpha.
/// It approximates Fluent's Shadow 08 / Shadow 28 without a blur effect, which in software
/// rendering is recomputed every animation frame and was ~40% of the cost of an expand.
/// </summary>
public sealed class SoftShadow : FrameworkElement
{
    private const int Steps = 7;

    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(
        nameof(Blur), typeof(double), typeof(SoftShadow), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
        nameof(OffsetY), typeof(double), typeof(SoftShadow), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(SoftShadow), new FrameworkPropertyMetadata(0.28, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(SoftShadow), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public SoftShadow()
    {
        IsHitTestVisible = false;
    }

    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public double OffsetY { get => (double)GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0 || Strength <= 0)
        {
            return;
        }

        // Outer layers are larger and fainter; together they read as a soft falloff.
        for (var i = Steps; i >= 1; i--)
        {
            var t = i / (double)Steps;
            var spread = Blur * t * 0.6;
            var alpha = Strength * (1 - t) * (1 - t) * 2.2 / Steps;
            if (alpha <= 0.002)
            {
                continue;
            }

            var rect = new Rect(-spread, OffsetY * (0.4 + 0.6 * t) - spread, size.Width + spread * 2, size.Height + spread * 2);
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, rect, Radius + spread, Radius + spread);
        }
    }
}
