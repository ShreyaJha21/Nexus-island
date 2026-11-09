using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Nexus.Modes.Controls;

/// <summary>
/// Draws one of the Nexus icons from design/icons (exported from the Figma file) in any color.
/// The icons are 20 x 20 Fluent-style line icons made only of paths with a 1.5 px round stroke
/// or a solid fill, so they scale cleanly to the 16 px compact size and 20 px panel size.
/// </summary>
public sealed class NexusIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(NexusIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(NexusIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double ViewBox = 20;
    private static readonly ConcurrentDictionary<string, IconPath[]> Cache = new();

    static NexusIcon()
    {
        WidthProperty.OverrideMetadata(typeof(NexusIcon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(NexusIcon), new FrameworkPropertyMetadata(16.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(NexusIcon), new FrameworkPropertyMetadata(true));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(NexusIcon), new UIPropertyMetadata(false));
    }

    /// <summary>File name in design/icons without extension, e.g. "timer", "bot", "attention".</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Kind) || ActualWidth <= 0)
        {
            return;
        }

        var paths = Cache.GetOrAdd(Kind, Load);
        var scale = Math.Min(ActualWidth, ActualHeight) / ViewBox;
        dc.PushTransform(new ScaleTransform(scale, scale));

        var pen = new Pen(Foreground, 1.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        foreach (var path in paths)
        {
            dc.DrawGeometry(path.Filled ? Foreground : null, path.Stroked ? pen : null, path.Geometry);
        }

        dc.Pop();
    }

    private static IconPath[] Load(string kind)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Nexus.Modes;component/Assets/Icons/{kind}.svg");
            var info = Application.GetResourceStream(uri);
            if (info is null)
            {
                return [];
            }

            using var stream = info.Stream;
            var doc = XDocument.Load(stream);
            XNamespace svg = "http://www.w3.org/2000/svg";

            return doc.Descendants(svg + "path")
                .Select(p =>
                {
                    var geometry = Geometry.Parse((string?)p.Attribute("d") ?? "");
                    geometry.Freeze();
                    var stroke = (string?)p.Attribute("stroke");
                    var fill = (string?)p.Attribute("fill");
                    return new IconPath(geometry,
                        Stroked: stroke is not null && stroke != "none",
                        Filled: fill is not null && fill != "none");
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private sealed record IconPath(Geometry Geometry, bool Stroked, bool Filled);
}
