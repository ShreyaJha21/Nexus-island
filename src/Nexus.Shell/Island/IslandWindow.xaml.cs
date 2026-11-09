using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nexus.Core.Settings;
using Nexus.Shell.Native;

namespace Nexus.Shell.Island;

public enum SurfaceShape
{
    /// <summary>The 64 x 6 resting line.</summary>
    Resting,
    /// <summary>The 32 px compact island.</summary>
    Pill,
    /// <summary>The 56 px banner (moments with an action).</summary>
    Banner,
    /// <summary>The 360 px expanded panel.</summary>
    Panel,
}

/// <summary>What the island should look like right now.</summary>
public sealed record Scene(SurfaceShape Shape, FrameworkElement? Content = null, bool Attention = false, FrameworkElement? Bubble = null);

/// <summary>
/// The island surface. It knows how to look and move (placement, rise and sink, the container
/// transform between pill and panel, the attention pulse) but not what to show; the
/// <see cref="IslandController"/> decides that and calls <see cref="Present"/>.
/// </summary>
public partial class IslandWindow : Window
{
    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static readonly Size RestingSize = new(64, 6);

    private IntPtr _hwnd;
    private FrameworkElement? _current;
    private FrameworkElement? _bubble;
    private SurfaceShape _shape = SurfaceShape.Resting;
    private bool _attention;
    private bool _anchorBottom = true;
    private int _visibilityToken;
    private double _clipRadius = 3;
    private int _sizeToken;

    /// <summary>Gap between the island and the taskbar (or the top of the screen).</summary>
    private const double StageGap = 8;

    /// <summary>Room above the island for its shadow and the "needs you" pulse ring.</summary>
    private const double ExtentMargin = 44;

    /// <summary>Current window height in DIPs; follows the island.</summary>
    private double _extent = 6 + StageGap + ExtentMargin;

    public IslandWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Reposition();
        Deactivated += (_, _) => KeyboardDeactivated?.Invoke();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                EscapePressed?.Invoke();
                e.Handled = true;
            }
        };
        MouseMove += (_, _) => PointerMoved?.Invoke();

        HitArea.MouseLeftButtonUp += (_, e) => { SurfaceClicked?.Invoke(); e.Handled = true; };
        HitArea.MouseRightButtonUp += (_, e) => { SurfaceRightClicked?.Invoke(); e.Handled = true; };
        HitArea.MouseWheel += (_, e) => { SurfaceWheel?.Invoke(e.Delta); e.Handled = true; };
        HitArea.MouseEnter += (_, _) => PointerEntered?.Invoke();
        HitArea.MouseLeave += (_, _) => PointerExited?.Invoke();
        Bubble.MouseLeftButtonUp += (_, e) => { BubbleClicked?.Invoke(); e.Handled = true; };

        ContentClip.SizeChanged += (_, e) => UpdateClip(e.NewSize);
        CurrentHost.ChildSizeChanged += (_, _) => RefitToContent();

        ApplyShape(SurfaceShape.Resting, reduced: true);
    }

    public event Action? SurfaceClicked;
    public event Action? SurfaceRightClicked;
    public event Action<int>? SurfaceWheel;
    public event Action? PointerEntered;
    public event Action? PointerExited;
    public event Action? PointerMoved;
    public event Action? BubbleClicked;
    public event Action? EscapePressed;
    public event Action? KeyboardDeactivated;

    public IntPtr Handle => _hwnd;

    public Placement Placement { get; set; } = Placement.AboveTaskbar;

    /// <summary>True when the island sits above the taskbar; false when it falls back to top-center.</summary>
    public bool AnchorBottom => _anchorBottom;

    public bool IsPointerOver => HitArea.IsMouseOver || Bubble.IsMouseOver;

    public UIElement SurfaceElement => Surface;

    // ------------------------------------------------------------------ Presenting

    public void Present(Scene scene)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            PresentCore(scene);
        }
        finally
        {
            DebugLog.Write($"Present {scene.Shape} {scene.Content?.GetType().Name} attention={scene.Attention} bubble={scene.Bubble?.GetType().Name} took {watch.Elapsed.TotalMilliseconds:N1} ms");
        }
    }

    private void PresentCore(Scene scene)
    {
        var reduced = Motion.Reduced;
        var previous = _current;
        var contentChanged = !ReferenceEquals(scene.Content, previous);

        if (contentChanged)
        {
            if (previous is not null)
            {
                CurrentHost.Children.Remove(previous);
                OutgoingHost.Children.Clear();
                OutgoingHost.Children.Add(previous);
            }

            if (scene.Content is not null)
            {
                Detach(scene.Content);
                scene.Content.BeginAnimation(OpacityProperty, null);
                scene.Content.Opacity = 0;
                CurrentHost.Children.Add(scene.Content);
            }

            _current = scene.Content;
        }

        var target = TargetSize(scene);
        var growing = Rank(scene.Shape) > Rank(_shape) ||
                      (Rank(scene.Shape) == Rank(_shape) && target.Width * target.Height > Surface.Width * Surface.Height + 1);

        ApplyShape(scene.Shape, reduced);
        AnimateSize(target, growing, reduced);
        if (contentChanged)
        {
            FadeContent(previous, scene.Content, growing, reduced);
        }

        SetAttention(scene.Attention);
        SetBubble(scene.Shape == SurfaceShape.Pill ? scene.Bubble : null, reduced);
        _shape = scene.Shape;
    }

    /// <summary>The signature "needs you" moment: one gentle orange pulse on the edge, then it holds still.</summary>
    public void Pulse()
    {
        if (Motion.Reduced)
        {
            return;
        }

        // Start the ring after the panel has mostly grown, so it pulses around the final shape.
        var start = Motion.ContentInDelay;
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = Motion.Pulse, BeginTime = start };
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.15), new CubicEase { EasingMode = EasingMode.EaseOut }));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));

        var scale = new DoubleAnimation(1, 1.06, Motion.Pulse) { BeginTime = start, EasingFunction = Motion.Decelerate };

        // A slow one-second fade looks the same at 30 fps and costs half as much to draw.
        Timeline.SetDesiredFrameRate(opacity, 30);
        Timeline.SetDesiredFrameRate(scale, 30);

        PulseRing.BeginAnimation(OpacityProperty, opacity);
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    // ------------------------------------------------------------------ Rise and sink

    /// <summary>Show: the island moves up 8 px out of the taskbar while fading in.</summary>
    public void Rise()
    {
        var token = ++_visibilityToken;
        if (!IsVisible)
        {
            Show();
            Reposition();
        }

        var from = _anchorBottom ? 8 : -8;
        if (Motion.Reduced)
        {
            StageShift.BeginAnimation(TranslateTransform.YProperty, null);
            StageShift.Y = 0;
            Stage.BeginAnimation(OpacityProperty, Motion.To(1, Motion.ReducedCrossfade));
            return;
        }

        if (Stage.Opacity < 0.01)
        {
            StageShift.BeginAnimation(TranslateTransform.YProperty, null);
            StageShift.Y = from;
        }

        StageShift.BeginAnimation(TranslateTransform.YProperty, Motion.To(0, Motion.Rise, Motion.Spring));
        Stage.BeginAnimation(OpacityProperty, Motion.To(1, TimeSpan.FromMilliseconds(150), Motion.Decelerate));
        _ = token;
    }

    /// <summary>Hide: the reverse of rise, 167 ms, accelerating out. The window is hidden afterwards.</summary>
    public void Sink(Action? completed = null)
    {
        var token = ++_visibilityToken;
        var to = _anchorBottom ? 8 : -8;
        var duration = Motion.Reduced ? Motion.ReducedCrossfade : Motion.Sink;

        var fade = Motion.To(0, duration, Motion.Accelerate);
        fade.Completed += (_, _) =>
        {
            if (token != _visibilityToken)
            {
                return; // risen again meanwhile
            }

            Hide();
            completed?.Invoke();
        };

        if (!Motion.Reduced)
        {
            StageShift.BeginAnimation(TranslateTransform.YProperty, Motion.To(to, duration, Motion.Accelerate));
        }

        Stage.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ Keyboard

    /// <summary>Nexus never takes keyboard focus, unless the user clicked to open the panel.</summary>
    public void EnableKeyboard()
    {
        SetExStyle(Win32.WS_EX_NOACTIVATE, false);
        Win32.SetForegroundWindow(_hwnd);
        Activate();
        Keyboard.Focus(this);
    }

    public void DisableKeyboard() => SetExStyle(Win32.WS_EX_NOACTIVATE, true);

    // ------------------------------------------------------------------ Placement

    /// <summary>
    /// Centered, floating just above the taskbar's top edge so taskbar icons stay uncovered.
    /// Falls back to top-center when the taskbar is on another edge or auto-hidden, or when chosen in settings.
    /// </summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bar = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        var found = Win32.SHAppBarMessage(Win32.ABM_GETTASKBARPOS, ref bar) != UIntPtr.Zero;
        var stateData = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        var autoHide = ((uint)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref stateData) & Win32.ABS_AUTOHIDE) != 0;

        var monitor = found
            ? Win32.MonitorFromRect(ref bar.rc, Win32.MONITOR_DEFAULTTOPRIMARY)
            : Win32.MonitorFromPoint(default, Win32.MONITOR_DEFAULTTOPRIMARY);
        var info = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);

        var bottom = Placement == Placement.AboveTaskbar && found && bar.uEdge == Win32.ABE_BOTTOM && !autoHide;
        SetAnchor(bottom);

        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(_extent * scale);
        var x = info.rcMonitor.Left + (info.rcMonitor.Width - width) / 2;
        var y = bottom ? bar.rc.Top - height : info.rcWork.Top;

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, width, height, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

        // Keep WPF's own Height in step so a later layout pass never resizes the window back.
        if (Math.Abs(Height - _extent) > 0.5)
        {
            Height = _extent;
        }

        DebugLog.Write($"Reposition took {watch.Elapsed.TotalMilliseconds:N1} ms");
    }

    public void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }

    // ------------------------------------------------------------------ Internals

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: hidden from Alt+Tab. No-activate: never steals focus.
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        style |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST;
        style &= ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(style));

        Reposition();
    }

    private void SetExStyle(long flag, bool on)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        style = on ? style | flag : style & ~flag;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(style));
    }

    private void SetAnchor(bool bottom)
    {
        _anchorBottom = bottom;
        var v = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Stage.VerticalAlignment = v;
        Stage.Margin = bottom ? new Thickness(0, 0, 0, 8) : new Thickness(0, 8, 0, 0);
        HitArea.VerticalAlignment = v;
        Surface.VerticalAlignment = v;
        Bubble.VerticalAlignment = v;
        CurrentHost.AnchorBottom = bottom;
        OutgoingHost.AnchorBottom = bottom;
        ApplyShape(_shape, reduced: true);
    }

    private Size TargetSize(Scene scene)
    {
        if (scene.Shape == SurfaceShape.Resting || scene.Content is null)
        {
            return RestingSize;
        }

        scene.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = scene.Content.DesiredSize;
        var height = scene.Shape switch
        {
            SurfaceShape.Pill => 32,
            SurfaceShape.Banner => 56,
            _ => Math.Min(Math.Ceiling(d.Height), 480),
        };

        return new Size(Math.Ceiling(d.Width), height);
    }

    /// <summary>The content changed size on its own (new track title, new agent detail): follow it.</summary>
    private void RefitToContent()
    {
        if (_current is null || _shape == SurfaceShape.Resting)
        {
            return;
        }

        var target = TargetSize(new Scene(_shape, _current));
        if (Math.Abs(target.Width - Surface.Width) < 0.5 && Math.Abs(target.Height - Surface.Height) < 0.5)
        {
            return;
        }

        DebugLog.Write($"Refit to {target} from {Surface.Width}x{Surface.Height}");
        AnimateSize(target, growing: true, Motion.Reduced);
    }

    private void ApplyShape(SurfaceShape shape, bool reduced)
    {
        var resting = shape == SurfaceShape.Resting;
        var radius = resting ? 3 : 8;
        _clipRadius = radius;
        SurfaceFill.CornerRadius = new CornerRadius(radius);
        SurfaceFill.BorderThickness = new Thickness(resting ? 0 : 1);
        HitArea.Background = resting ? HitBrush : Brushes.Transparent;
        HitArea.Padding = resting
            ? (_anchorBottom ? new Thickness(14, 12, 14, 0) : new Thickness(14, 0, 14, 12))
            : new Thickness(0);
        Cursor = resting ? Cursors.Arrow : null;
        UpdateClip(ContentClip.RenderSize);

        // Fluent Shadow 08 under the compact island, Shadow 28 under the expanded panel.
        var large = shape is SurfaceShape.Panel or SurfaceShape.Banner;
        Shadow.Radius = radius;
        if (reduced)
        {
            Shadow.BeginAnimation(SoftShadow.BlurProperty, null);
            Shadow.BeginAnimation(SoftShadow.OffsetYProperty, null);
            Shadow.BeginAnimation(SoftShadow.StrengthProperty, null);
            Shadow.Blur = large ? 28 : 8;
            Shadow.OffsetY = large ? 14 : 4;
            Shadow.Strength = large ? 0.44 : 0.28;
            return;
        }

        Shadow.BeginAnimation(SoftShadow.BlurProperty, Motion.To(large ? 28 : 8, Motion.Expand));
        Shadow.BeginAnimation(SoftShadow.OffsetYProperty, Motion.To(large ? 14 : 4, Motion.Expand));
        Shadow.BeginAnimation(SoftShadow.StrengthProperty, Motion.To(large ? 0.44 : 0.28, Motion.Expand));
    }

    private void AnimateSize(Size target, bool growing, bool reduced)
    {
        var token = ++_sizeToken;

        // Make room first; the window never needs to be taller than what is on screen.
        FitWindow(Math.Max(target.Height, Surface.Height), allowShrink: false);

        if (reduced)
        {
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            Surface.Width = target.Width;
            Surface.Height = target.Height;
            FitWindow(target.Height, allowShrink: true);
            return;
        }

        // Expand: the island's own shape grows into the panel on a spring.
        // Collapse: 167 ms, after the content has started fading out.
        var duration = growing ? Motion.Expand : Motion.Collapse;
        var ease = growing ? Motion.Spring : Motion.Standard;
        var delay = growing ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40);
        var height = Motion.To(target.Height, duration, ease, delay);
        height.Completed += (_, _) =>
        {
            if (token == _sizeToken)
            {
                // Shrink the window back once the shape has settled (after the pulse ring has faded).
                Dispatcher.BeginInvoke(() => { if (token == _sizeToken) FitWindow(target.Height, allowShrink: true); },
                    DispatcherPriority.Background);
            }
        };
        Surface.BeginAnimation(WidthProperty, Motion.To(target.Width, duration, ease, delay), HandoffBehavior.SnapshotAndReplace);
        Surface.BeginAnimation(HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Sizes the transparent window to the island (plus room for the shadow and pulse ring).
    /// A layered window is redrawn and copied whole on every frame, so a window that hugs the
    /// island is roughly ten times cheaper to animate than one sized for the largest panel.
    /// </summary>
    private void FitWindow(double surfaceHeight, bool allowShrink)
    {
        var needed = Math.Ceiling(surfaceHeight + StageGap + ExtentMargin);
        if (needed > _extent + 0.5 || (allowShrink && needed < _extent - 0.5))
        {
            DebugLog.Write($"FitWindow {_extent} -> {needed}");
            _extent = needed;
            Reposition();
        }
    }

    private void FadeContent(FrameworkElement? previous, FrameworkElement? next, bool growing, bool reduced)
    {
        if (previous is not null)
        {
            var fadeOut = Motion.To(0, reduced ? Motion.ReducedCrossfade : Motion.ContentOut);
            fadeOut.Completed += (_, _) =>
            {
                if (!ReferenceEquals(previous, _current) && previous.Parent is Panel p && ReferenceEquals(p, OutgoingHost))
                {
                    OutgoingHost.Children.Remove(previous);
                }
            };
            previous.BeginAnimation(OpacityProperty, fadeOut);
        }

        if (next is not null)
        {
            var delay = reduced ? TimeSpan.Zero : growing ? Motion.ContentInDelay : TimeSpan.FromMilliseconds(120);
            var duration = reduced ? Motion.ReducedCrossfade : Motion.ContentIn;
            next.BeginAnimation(OpacityProperty, Motion.To(1, duration, Motion.Decelerate, delay));
        }
    }

    private void SetAttention(bool on)
    {
        if (on == _attention)
        {
            return;
        }

        _attention = on;
        AttentionEdge.BeginAnimation(OpacityProperty, Motion.To(on ? 1 : 0, TimeSpan.FromMilliseconds(150)));
    }

    private void SetBubble(FrameworkElement? bubble, bool reduced)
    {
        if (ReferenceEquals(bubble, _bubble))
        {
            return;
        }

        _bubble = bubble;
        var duration = reduced ? Motion.ReducedCrossfade : TimeSpan.FromMilliseconds(150);

        if (bubble is null)
        {
            var fade = Motion.To(0, duration);
            fade.Completed += (_, _) =>
            {
                if (_bubble is null)
                {
                    BubbleContent.Content = null;
                    Bubble.Visibility = Visibility.Collapsed;
                }
            };
            Bubble.BeginAnimation(OpacityProperty, fade);
            return;
        }

        Detach(bubble);
        BubbleContent.Content = bubble;
        Bubble.Visibility = Visibility.Visible;
        Bubble.BeginAnimation(OpacityProperty, Motion.To(1, duration, Motion.Decelerate, reduced ? null : TimeSpan.FromMilliseconds(100)));
    }

    private void UpdateClip(Size size) =>
        ContentClip.Clip = new RectangleGeometry(new Rect(size), _clipRadius, _clipRadius);

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent ?? VisualTreeHelper.GetParent(element))
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentPresenter presenter:
                presenter.Content = null;
                break;
            case ContentControl control:
                control.Content = null;
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }

    private static int Rank(SurfaceShape shape) => shape switch
    {
        SurfaceShape.Resting => 0,
        SurfaceShape.Pill => 1,
        SurfaceShape.Banner => 2,
        _ => 3,
    };

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
