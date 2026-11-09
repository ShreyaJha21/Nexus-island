using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Nexus.Modes.Controls;

/// <summary>
/// The three-bar level meter from the music pill (2 px bars, 2 px gap, 12 px tall).
/// It gently animates while <see cref="IsActive"/> and rests at the design's static heights otherwise.
/// The animation runs at a low frame rate to keep idle CPU near zero.
/// </summary>
public sealed class LevelMeter : StackPanel
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LevelMeter), new PropertyMetadata(false, (d, _) => ((LevelMeter)d).Update()));

    private static readonly double[] RestHeights = [6, 11, 8];
    private readonly Rectangle[] _bars;

    public LevelMeter()
    {
        Orientation = Orientation.Horizontal;
        Height = 12;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        _bars = RestHeights.Select((h, i) => new Rectangle
        {
            Width = 2,
            Height = h,
            RadiusX = 1,
            RadiusY = 1,
            Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        }).ToArray();

        foreach (var bar in _bars)
        {
            bar.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            Children.Add(bar);
        }

        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void Update()
    {
        if (IsActive && IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        double[][] frames = [[6, 11, 8], [10, 5, 11], [4, 9, 6], [11, 7, 3], [7, 12, 9]];
        for (var i = 0; i < _bars.Length; i++)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true,
                Duration = TimeSpan.FromMilliseconds(1300 + i * 170),
            };
            Timeline.SetDesiredFrameRate(anim, 15);
            for (var f = 0; f < frames.Length; f++)
            {
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(frames[f][i],
                    KeyTime.FromPercent(f / (double)(frames.Length - 1)),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
            }

            _bars[i].BeginAnimation(HeightProperty, anim);
        }
    }

    private void Stop()
    {
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i].BeginAnimation(HeightProperty, null);
            _bars[i].Height = RestHeights[i];
        }
    }
}
