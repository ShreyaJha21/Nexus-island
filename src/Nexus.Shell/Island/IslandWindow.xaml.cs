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

/// <summary>Which side of its window the island hugs: the screen edge it sits against, or neither.</summary>
internal enum HAlign { Left, Center, Right }

/// <summary>
/// Where the island's window goes for a placement, in physical pixels, and how the island sits in it.
/// <see cref="Spot"/> is the placement actually used: a spot that is not possible right now (no room in
/// the taskbar, taskbar on another edge) falls back to one that is.
/// </summary>
internal readonly record struct Geometry(
    Placement Spot, IntPtr Monitor, double Scale, int X, int Y, int Width, int Height,
    bool AnchorBottom, bool FlushTop, HAlign Align, bool Docked,
    Win32.RECT? Footprint, int RestX, int RestY);

/// <summary>What the island should look like right now.</summary>
/// <param name="AttentionBrush">Colour of the "needs you" edge and pulse; null for the standard attention orange.</param>
public sealed record Scene(SurfaceShape Shape, FrameworkElement? Content = null, bool Attention = false, FrameworkElement? Bubble = null,
    Brush? AttentionBrush = null);

/// <summary>
/// The island surface. It knows how to look and move (placement, rise and sink, the container
/// transform between pill and panel, the attention pulse) but not what to show; the
/// <see cref="IslandController"/> decides that and calls <see cref="Present"/>.
/// </summary>
public partial class IslandWindow : Window
{
    // Alpha 2 rather than 1: invisible, but safely above zero so the layered window takes the pointer.
    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(2, 0, 0, 0)));
    /// <summary>
    /// The resting line. Figma specifies 64 x 6, but a near-black 6 px line disappears against a dark
    /// taskbar, so it is larger and carries a light outline to stay findable on any background.
    /// </summary>
    private static readonly Size RestingSize = new(112, 10);
    private const double RestingRadius = 5;

    /// <summary>How far the resting line's hover target reaches past each end of the line.</summary>
    private const double RestingReachSide = 48;

    /// <summary>How far the resting line's hover target reaches away from the screen edge it sits against.</summary>
    private const double RestingReachAway = 22;

    /// <summary>The same for the 32 px pill: a little extra above, and down to the screen edge.</summary>
    private const double PillReachAway = 6;

    private IntPtr _hwnd;
    private FrameworkElement? _current;
    private FrameworkElement? _bubble;
    private SurfaceShape _shape = SurfaceShape.Resting;
    private bool _attention;
    private bool _anchorBottom = true;
    private bool _flushTop;
    private bool _docked;
    private HAlign _align = HAlign.Center;
    private Geometry? _geometry;
    private Placement? _moved;
    private int _glideToken;
    private int _visibilityToken;
    private double _clipRadius = RestingRadius;
    private int _sizeToken;

    /// <summary>Gap between the island and the taskbar (or the top of the screen).</summary>
    private const double StageGap = 8;

    /// <summary>Distance from the side of the screen in the bottom-left and bottom-right spots.</summary>
    private const double EdgeInset = 12;

    /// <summary>The area the compact island can take up at a spot: what must be clear of an app's controls.</summary>
    private static readonly Size FootprintSize = new(400, 46);

    /// <summary>Room above the island for its shadow and the "needs you" pulse ring.</summary>
    private const double ExtentMargin = 44;

    /// <summary>Current window height in DIPs; follows the island.</summary>
    private double _extent = 10 + StageGap + ExtentMargin;

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

        // Things dragged from other apps (a photo from a browser, files from Explorer) land on the shelf.
        AllowDrop = true;
        DragEnter += (_, e) => OnDropDrag(e, entering: true);
        DragOver += (_, e) => OnDropDrag(e, entering: false);
        DragLeave += (_, e) => { DropLeft?.Invoke(); e.Handled = true; };
        Drop += (_, e) =>
        {
            e.Effects = _dropAccepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (_dropAccepted)
            {
                _dropAccepted = false;
                Dropped?.Invoke(e.Data);
            }
        };

        HitArea.PreviewMouseLeftButtonDown += OnDragArm;
        HitArea.PreviewMouseMove += OnDragMove;
        HitArea.PreviewMouseLeftButtonUp += OnDragRelease;
        HitArea.LostMouseCapture += (_, _) => { if (_dragging) EndDrag(); };

        ContentClip.SizeChanged += (_, e) => UpdateClip(e.NewSize);
        Surface.SizeChanged += (_, _) =>
        {
            UpdateCorners();
            SyncBackdrop();
        };
        IsVisibleChanged += (_, _) => SyncBackdrop();
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

    /// <summary>The user picked the island up.</summary>
    public event Action? DragStarted;

    /// <summary>Something from another app is dragged over the island. Return true to accept it.</summary>
    public event Func<IDataObject, bool>? DropEntered;

    /// <summary>The drag moved off the island (or a part of it; the controller waits a moment to be sure).</summary>
    public event Action? DropLeft;

    /// <summary>It was let go over the island.</summary>
    public event Action<IDataObject>? Dropped;

    private bool _dropAccepted;

    private void OnDropDrag(DragEventArgs e, bool entering)
    {
        if (entering || !_dropAccepted)
        {
            _dropAccepted = DropEntered?.Invoke(e.Data) == true;
        }

        e.Effects = _dropAccepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// The user dropped the island: the placement it landed on (a named spot when dropped close to one,
    /// otherwise Custom), with its centre as a fraction of the screen's work area and that screen's name.
    /// </summary>
    public event Action<Placement, double, double, string?>? DragEnded;

    public IntPtr Handle => _hwnd;

    /// <summary>The blur behind the island, or null when transparency effects are off.</summary>
    internal Backdrop.Kind BackdropKind => _backdrop?.Mode ?? Backdrop.Kind.None;

    private Backdrop? _backdrop;
    private DateTime _syncUntil;
    private bool _syncing;

    /// <summary>
    /// Moves the blur window to exactly cover the island surface. Size changes arrive through
    /// SizeChanged; moves that happen in a render transform (rise, sink) are followed frame by frame
    /// for a short while via <see cref="KeepBackdropSynced"/>.
    /// </summary>
    private void SyncBackdrop()
    {
        if (_backdrop is null)
        {
            return;
        }

        if (!IsVisible || Stage.Opacity < 0.95 || Surface.ActualWidth < 1 || PresentationSource.FromVisual(Surface) is null)
        {
            _backdrop.Hide();
            return;
        }

        // The blur window is moved on the UI thread while the island is drawn on the render thread, so
        // it can trail a frame behind. During a size animation, give it the smaller of the current and
        // final shape: then the lag always falls inside the island and blur never pokes out of its edges.
        var w = Surface.ActualWidth;
        var h = Surface.ActualHeight;
        if (_animTarget is { } target)
        {
            w = Math.Min(w, target.Width);
            h = Math.Min(h, target.Height);
        }

        // Keep it 1 px inside the island's border, so the antialiased edge never shows a blur halo.
        const double inset = 1;
        var left = _align switch
        {
            HAlign.Left => inset,
            HAlign.Right => Surface.ActualWidth - w + inset,
            _ => (Surface.ActualWidth - w) / 2 + inset,
        };
        var top = _anchorBottom ? Surface.ActualHeight - h + inset : _flushTop ? 0 : inset;
        var topLeft = Surface.PointToScreen(new Point(left, top));
        var bottomRight = Surface.PointToScreen(new Point(left + w - inset * 2, top + h - inset * (_flushTop ? 1 : 2)));
        var radius = Math.Max(1, Math.Min(h / 2, PanelRadius) - inset);
        _backdrop.Place(
            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y),
            _hwnd, radius: (int)Math.Round(radius * _scale), squareTop: _flushTop);
    }

    private void KeepBackdropSynced(TimeSpan duration)
    {
        if (_backdrop is null)
        {
            return;
        }

        var until = DateTime.UtcNow + duration;
        if (until > _syncUntil)
        {
            _syncUntil = until;
        }

        if (!_syncing)
        {
            _syncing = true;
            CompositionTarget.Rendering += OnRenderingSync;
        }
    }

    private void OnRenderingSync(object? sender, EventArgs e)
    {
        SyncBackdrop();
        if (DateTime.UtcNow > _syncUntil)
        {
            CompositionTarget.Rendering -= OnRenderingSync;
            _syncing = false;
            SyncBackdrop();
        }
    }

    /// <summary>Where the user wants the island. Change it with <see cref="MoveTo"/> once the window is up.</summary>
    public Placement Placement { get; set; } = Placement.AboveTaskbar;

    /// <summary>Custom placement: the island's centre as a fraction of the work area of <see cref="CustomMonitor"/>.</summary>
    public double CustomX { get; set; } = 0.5;
    public double CustomY { get; set; } = 0.9;
    public string? CustomMonitor { get; set; }

    /// <summary>The spot the island moved to because its own was taken, or null when it is where the user wants it.</summary>
    public Placement? MovedTo => _moved;

    private Placement Effective => _moved ?? Placement;

    /// <summary>True when the island grows upward (it sits in the lower part of the screen).</summary>
    public bool AnchorBottom => _anchorBottom;

    /// <summary>The user may pick the island up and drop it somewhere else. Only while it is compact.</summary>
    public bool CanDrag { get; set; }

    public bool IsDragging => _dragging;

    /// <summary>
    /// Whether the pointer is over the island. WPF's own IsMouseOver can lag behind while the window is
    /// resized under the pointer, so this also checks the pointer's actual position.
    /// </summary>
    public bool IsPointerOver => HitArea.IsMouseOver || Bubble.IsMouseOver || CursorInside(HitArea) || CursorInside(Bubble);

    private bool CursorInside(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || PresentationSource.FromVisual(element) is null ||
            !Win32.GetCursorPos(out var cursor))
        {
            return false;
        }

        var p = element.PointFromScreen(new Point(cursor.X, cursor.Y));
        return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
    }

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

        if (scene.Attention)
        {
            // Whoever needs you colours the edge and the pulse: Claude's own colour for Claude Code.
            var edge = scene.AttentionBrush ?? (Brush)FindResource("Brush.Attention");
            AttentionEdge.BorderBrush = edge;
            PulseRing.BorderBrush = edge;
        }

        SetAttention(scene.Attention);
        _sceneBubble = scene.Bubble;
        SetBubble(scene.Shape == SurfaceShape.Pill && !_docked ? scene.Bubble : null, reduced); // no room for a bubble in the taskbar
        _shape = scene.Shape;
        KeepBackdropSynced(TimeSpan.FromMilliseconds(450));
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
        }

        Reposition();

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
        KeepBackdropSynced(TimeSpan.FromMilliseconds(400));
        _ = token;
    }

    /// <summary>Hide: the reverse of rise, 167 ms, accelerating out. The window is hidden afterwards.</summary>
    public void Sink(Action? completed = null)
    {
        var token = ++_visibilityToken;
        var to = _anchorBottom ? 8 : -8;
        var duration = Motion.Reduced ? Motion.ReducedCrossfade : Motion.Sink;
        _backdrop?.Hide(); // the blur can't fade, so it goes first and the tint fades out over nothing

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
    /// Puts the window where the island belongs right now: its preferred spot, or the one it moved to.
    /// A running glide ends here.
    /// </summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _glideToken++;

        var g = Resolve(Effective, _dock);
        if (ApplyLayout(g))
        {
            g = Resolve(Effective, _dock); // the new layout may have resized the island
        }

        _geometry = g;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, g.X, g.Y, g.Width, g.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);

        // Keep WPF's own Height in step so a later layout pass never resizes the window back.
        if (Math.Abs(Height - _extent) > 0.5)
        {
            Height = _extent;
        }

        DebugLog.Write($"Reposition {g.Spot} took {watch.Elapsed.TotalMilliseconds:N1} ms");
        KeepBackdropSynced(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Works out where the window goes for a placement.
    /// <list type="bullet">
    /// <item>Bottom centre, left and right float just above the taskbar's top edge, so taskbar icons stay uncovered.</item>
    /// <item>Top centre hangs flush from the top edge.</item>
    /// <item>Taskbar tucks into the given free stretch of the taskbar.</item>
    /// <item>Custom is wherever the user dropped it; it grows toward the roomier side of the screen.</item>
    /// </list>
    /// The bottom spots fall back to top centre when the taskbar is on another edge or auto-hidden.
    /// </summary>
    private Geometry Resolve(Placement spot, TaskbarGap? gap)
    {
        var bar = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        var found = Win32.SHAppBarMessage(Win32.ABM_GETTASKBARPOS, ref bar) != UIntPtr.Zero;
        var stateData = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        var autoHide = ((uint)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref stateData) & Win32.ABS_AUTOHIDE) != 0;
        var bottomOk = found && bar.uEdge == Win32.ABE_BOTTOM && !autoHide;

        if (spot == Placement.Taskbar && (gap is null || !bottomOk))
        {
            spot = Placement.AboveTaskbar;
        }

        if (spot is Placement.AboveTaskbar or Placement.BottomLeft or Placement.BottomRight && !bottomOk)
        {
            spot = Placement.TopCenter;
        }

        var monitor = found
            ? Win32.MonitorFromRect(ref bar.rc, Win32.MONITOR_DEFAULTTOPRIMARY)
            : Win32.MonitorFromPoint(default, Win32.MONITOR_DEFAULTTOPRIMARY);
        if (spot == Placement.Custom && Win32.MonitorByName(CustomMonitor) is var named && named != IntPtr.Zero)
        {
            monitor = named;
        }

        var info = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        int Px(double dips) => (int)Math.Round(dips * scale);

        var work = info.rcWork;
        var screen = info.rcMonitor;
        var width = Px(Width);
        var height = Px(_extent);
        var stageGap = Px(StageGap);
        var edge = Px(EdgeInset);
        var restW = Px(RestingSize.Width);
        var restH = Px(RestingSize.Height);
        var footW = Px(FootprintSize.Width);
        var footH = Px(FootprintSize.Height);
        var centerX = screen.Left + screen.Width / 2;

        switch (spot)
        {
            case Placement.TopCenter:
            {
                var foot = new Win32.RECT { Left = centerX - footW / 2, Right = centerX + footW / 2, Top = work.Top, Bottom = work.Top + footH };
                return new Geometry(spot, monitor, scale, screen.Left + (screen.Width - width) / 2, work.Top, width, height,
                    AnchorBottom: false, FlushTop: true, HAlign.Center, Docked: false, foot, centerX, work.Top + restH / 2);
            }

            case Placement.Taskbar:
            {
                // The island sits in the empty part of the taskbar, vertically centered on it, at the
                // start of the gap; panels open upward and to the right from there.
                var dock = gap!.Value;
                var x = dock.Left + Px(4);
                var y = dock.CenterY + Px(16) + stageGap - height;
                return new Geometry(spot, monitor, scale, x, y, width, height,
                    AnchorBottom: true, FlushTop: false, HAlign.Left, Docked: true, null, x + restW / 2, dock.CenterY);
            }

            case Placement.Custom:
            {
                // The saved point is the centre of the resting line. Keep the line fully on screen.
                var cx = work.Left + (int)Math.Round(CustomX * work.Width);
                var cy = work.Top + (int)Math.Round(CustomY * work.Height);
                cx = Math.Clamp(cx, work.Left + edge + restW / 2, Math.Max(work.Left + edge + restW / 2, work.Right - edge - restW / 2));
                cy = Math.Clamp(cy, work.Top + restH / 2, Math.Max(work.Top + restH / 2, work.Bottom - stageGap - restH / 2));

                // Panels grow upward when there is room above (or more room than below), else downward.
                var above = cy - work.Top;
                var bottom = above >= Px(500) || above >= work.Bottom - cy;

                // Near a side of the screen the island hugs that side, so panels open inward.
                var align = HAlign.Center;
                var x = cx - width / 2;
                if (x < work.Left + edge)
                {
                    align = HAlign.Left;
                    x = cx - restW / 2;
                }
                else if (x + width > work.Right - edge)
                {
                    align = HAlign.Right;
                    x = cx + restW / 2 - width;
                }

                var footLeft = align switch { HAlign.Left => x, HAlign.Right => x + width - footW, _ => cx - footW / 2 };
                int y, footTop;
                if (bottom)
                {
                    var surfaceBottom = cy + restH / 2;
                    y = surfaceBottom + stageGap - height;
                    footTop = surfaceBottom + stageGap - footH;
                }
                else
                {
                    var surfaceTop = cy - restH / 2;
                    y = surfaceTop - stageGap;
                    footTop = y;
                }

                var foot = new Win32.RECT { Left = footLeft, Right = footLeft + footW, Top = footTop, Bottom = footTop + footH };
                return new Geometry(spot, monitor, scale, x, y, width, height,
                    bottom, FlushTop: false, align, Docked: false, foot, cx, cy);
            }

            default:
            {
                var align = spot switch { Placement.BottomLeft => HAlign.Left, Placement.BottomRight => HAlign.Right, _ => HAlign.Center };
                var x = align switch
                {
                    HAlign.Left => work.Left + edge,
                    HAlign.Right => work.Right - edge - width,
                    _ => screen.Left + (screen.Width - width) / 2,
                };
                var footLeft = align switch { HAlign.Left => x, HAlign.Right => x + width - footW, _ => centerX - footW / 2 };
                var restX = align switch { HAlign.Left => x + restW / 2, HAlign.Right => x + width - restW / 2, _ => centerX };
                var foot = new Win32.RECT { Left = footLeft, Right = footLeft + footW, Top = bar.rc.Top - footH, Bottom = bar.rc.Top };
                return new Geometry(spot, monitor, scale, x, bar.rc.Top - height, width, height,
                    AnchorBottom: true, FlushTop: false, align, Docked: false, foot, restX, bar.rc.Top - stageGap - restH / 2);
            }
        }
    }

    /// <summary>
    /// Lays the island out for a geometry: which way it grows, which side it hugs, whether it hangs
    /// flush from the top edge or sits in the taskbar. Returns true when something changed.
    /// </summary>
    private bool ApplyLayout(Geometry g)
    {
        _scale = g.Scale;
        if (_layoutApplied && g.AnchorBottom == _anchorBottom && g.FlushTop == _flushTop && g.Align == _align && g.Docked == _docked)
        {
            return false;
        }

        var dockChanged = g.Docked != _docked;
        _layoutApplied = true;
        _anchorBottom = g.AnchorBottom;
        _flushTop = g.FlushTop;
        _align = g.Align;
        _docked = g.Docked;

        // Above the taskbar the island floats 8 px clear of it. At the top it hangs flush from the
        // screen edge, like a MacBook's notch, and grows downward from there.
        var v = _anchorBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Stage.VerticalAlignment = v;
        Stage.Margin = _anchorBottom ? new Thickness(0, 0, 0, StageGap) : _flushTop ? new Thickness(0) : new Thickness(0, StageGap, 0, 0);
        HitArea.VerticalAlignment = v;
        Surface.VerticalAlignment = v;
        Bubble.VerticalAlignment = v;
        CurrentHost.AnchorBottom = _anchorBottom;
        OutgoingHost.AnchorBottom = _anchorBottom;

        // Centered on the screen, or hugging the left or right side of its window.
        Stage.ColumnDefinitions[0].Width = _align == HAlign.Left ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Stage.ColumnDefinitions[2].Width = _align == HAlign.Right ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        CurrentHost.AnchorLeft = _align == HAlign.Left;
        OutgoingHost.AnchorLeft = _align == HAlign.Left;

        ApplyShape(_shape, reduced: true);
        SetBubble(_docked || _shape != SurfaceShape.Pill ? null : _sceneBubble, reduced: true);

        // A taskbar gap can be narrower than the island: refit it.
        if (dockChanged && (_current is not null || _shape == SurfaceShape.Resting))
        {
            AnimateSize(TargetSize(new Scene(_shape, _current)), growing: true, reduced: true);
        }

        return true;
    }

    // ------------------------------------------------------------------ Moving between spots

    private TaskbarGap? _dock;
    private bool _layoutApplied;
    private double _scale = 1;
    private FrameworkElement? _sceneBubble;

    private static readonly Brush OverflowFade = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Colors.Black, 0),
            new(Colors.Black, 0.8),
            new(Colors.Transparent, 1),
        },
        new Point(0, 0), new Point(1, 0)));

    /// <summary>True while the island is tucked into the taskbar.</summary>
    public bool IsDocked => _docked;

    /// <summary>Display scale of the island's monitor (1.25 at 125%).</summary>
    public double ScaleFactor => _scale;

    /// <summary>
    /// The screen area (physical pixels) the compact island takes up at a spot, or null when that spot
    /// is not available right now. The taskbar spot has none: nothing of an app is ever under it.
    /// </summary>
    internal Win32.RECT? Footprint(Placement spot)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return null;
        }

        var g = Resolve(spot, null);
        return g.Spot == spot ? g.Footprint : null;
    }

    /// <summary>
    /// Sets where the island lives: the user's preferred spot, plus the spot it has moved to because
    /// its own is taken (null: it is where the user wants it) and the taskbar gap to use for the
    /// taskbar spot. The island glides to the new place; going into or out of the taskbar or the top
    /// edge, where it changes shape, it sinks away and rises in its new place instead.
    /// </summary>
    internal void MoveTo(Placement preferred, Placement? moved, TaskbarGap? gap)
    {
        if (moved == preferred)
        {
            moved = null;
        }

        var target = Resolve(moved ?? preferred, gap);
        var current = _geometry;

        Placement = preferred;
        _moved = moved;
        _dock = gap;

        if (current is { } c && c.Spot == target.Spot && c.X == target.X && c.Y == target.Y && c.Align == target.Align && !_dragDropped)
        {
            return; // already there
        }

        _dragDropped = false;
        DebugLog.Write($"Move to {target.Spot} ({target.X},{target.Y})");

        if (current is not { } from || !IsVisible || Stage.Opacity < 0.5 || Motion.Reduced)
        {
            Reposition();
            return;
        }

        // Different shape or a different screen: no straight path between the two, so sink and rise.
        if (from.FlushTop || target.FlushTop || from.Docked || target.Docked ||
            from.Monitor != target.Monitor || from.AnchorBottom != target.AnchorBottom)
        {
            Sink(() =>
            {
                Reposition();
                Rise();
            });
            return;
        }

        // Same shape: glide. Lay out for the new spot first, then start the window where the island
        // appears to be now (the layout change may have shifted it inside the window).
        var before = SurfaceOffset();
        _glideToken++;
        if (ApplyLayout(target))
        {
            target = Resolve(Effective, _dock);
        }

        _geometry = target;
        UpdateLayout();
        var after = SurfaceOffset();
        Win32.GetWindowRect(_hwnd, out var now);
        Glide(now.Left + before.X - after.X, now.Top + before.Y - after.Y, target);
    }

    /// <summary>The island's top-left corner inside the window, in physical pixels.</summary>
    private (int X, int Y) SurfaceOffset()
    {
        var p = Surface.TranslatePoint(new Point(0, 0), this);
        return ((int)Math.Round(p.X * _scale), (int)Math.Round(p.Y * _scale));
    }

    /// <summary>State 4, repositioning: the window slides to its new spot on a spring, the blur following.</summary>
    private void Glide(int fromX, int fromY, Geometry to)
    {
        var token = ++_glideToken;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, fromX, fromY, to.Width, to.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        SyncBackdrop();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (token != _glideToken)
            {
                CompositionTarget.Rendering -= tick;
                return;
            }

            var t = Math.Min(1, watch.Elapsed.TotalMilliseconds / Motion.Glide.TotalMilliseconds);
            var eased = Motion.Spring.Ease(t);
            var x = (int)Math.Round(fromX + (to.X - fromX) * eased);
            var y = (int)Math.Round(fromY + (to.Y - fromY) * eased);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            SyncBackdrop();

            if (t >= 1)
            {
                CompositionTarget.Rendering -= tick;
            }
        };
        CompositionTarget.Rendering += tick;
    }

    // ------------------------------------------------------------------ Dragging

    private bool _dragArmed;
    private bool _dragging;
    private bool _dragDropped;
    private Win32.POINT _dragFrom;
    private Win32.RECT _dragWindow;

    private void OnDragArm(object sender, MouseButtonEventArgs e)
    {
        // Buttons on the island (play, next) keep their clicks.
        if (!CanDrag || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Win32.GetCursorPos(out _dragFrom);
        Win32.GetWindowRect(_hwnd, out _dragWindow);
        _dragArmed = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed = false;
            if (_dragging)
            {
                EndDrag();
            }

            return;
        }

        Win32.GetCursorPos(out var now);
        var dx = now.X - _dragFrom.X;
        var dy = now.Y - _dragFrom.Y;
        if (!_dragging)
        {
            // A few pixels of slack, so a slightly shaky click still opens the island.
            var slack = 6 * _scale;
            if (Math.Abs(dx) < slack && Math.Abs(dy) < slack)
            {
                return;
            }

            _dragging = true;
            _glideToken++;
            HitArea.CaptureMouse();
            DebugLog.Write("Drag started");
            DragStarted?.Invoke();
        }

        Win32.SetWindowPos(_hwnd, IntPtr.Zero, _dragWindow.Left + dx, _dragWindow.Top + dy, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        SyncBackdrop();
    }

    private void OnDragRelease(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (_dragging)
        {
            e.Handled = true; // a drop is not a click
            EndDrag();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragArmed = false;
        HitArea.ReleaseMouseCapture();

        // Where the resting line would be now: that point is what gets remembered.
        var topLeft = Surface.PointToScreen(new Point(0, 0));
        var bottomRight = Surface.PointToScreen(new Point(Surface.ActualWidth, Surface.ActualHeight));
        var restW = RestingSize.Width * _scale;
        var restH = RestingSize.Height * _scale;
        var cx = _align switch
        {
            HAlign.Left => topLeft.X + restW / 2,
            HAlign.Right => bottomRight.X - restW / 2,
            _ => (topLeft.X + bottomRight.X) / 2,
        };
        var cy = _anchorBottom ? bottomRight.Y - restH / 2 : topLeft.Y + restH / 2;

        // Dropped close to a named spot: snap to it, so "back to the middle" is easy to hit.
        var placement = Placement.Custom;
        foreach (var spot in new[] { Placement.AboveTaskbar, Placement.BottomLeft, Placement.BottomRight, Placement.TopCenter })
        {
            var g = Resolve(spot, null);
            if (g.Spot == spot && Math.Abs(g.RestX - cx) < 48 * _scale && Math.Abs(g.RestY - cy) < 28 * _scale)
            {
                placement = spot;
                break;
            }
        }

        var monitor = Win32.MonitorFromPoint(new Win32.POINT { X = (int)cx, Y = (int)cy }, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var work = info.rcWork;
        var fx = work.Width > 0 ? Math.Clamp((cx - work.Left) / work.Width, 0, 1) : 0.5;
        var fy = work.Height > 0 ? Math.Clamp((cy - work.Top) / work.Height, 0, 1) : 0.9;

        _dragDropped = true; // the window is no longer where the last geometry put it
        DebugLog.Write($"Dropped at {cx:N0},{cy:N0} -> {placement} {fx:N3},{fy:N3}");
        DragEnded?.Invoke(placement, fx, fy, Win32.MonitorName(monitor));
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase || Nexus.Modes.Controls.IslandInput.GetOwnsDrag(node))
            {
                return true; // buttons keep their clicks; the shelf's items drag out instead
            }
        }

        return false;
    }

    /// <summary>Width available to the compact island inside the taskbar gap, in DIPs.</summary>
    private double DockedMaxWidth => _docked && _dock is { } gap ? Math.Max(60, (gap.Width - 8 * _scale) / _scale) : double.PositiveInfinity;

    public void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            SyncBackdrop(); // keep the blur directly beneath the island
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _backdrop?.Dispose();
        base.OnClosed(e);
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

        _backdrop = Backdrop.TryCreate();
        DebugLog.Write($"Backdrop: {BackdropKind}");
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

    private Size TargetSize(Scene scene)
    {
        if (scene.Shape == SurfaceShape.Resting || scene.Content is null)
        {
            ContentClip.OpacityMask = null;
            return new Size(Math.Min(RestingSize.Width, DockedMaxWidth), RestingSize.Height);
        }

        scene.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = scene.Content.DesiredSize;
        var height = scene.Shape switch
        {
            SurfaceShape.Pill => 32,
            SurfaceShape.Banner => 56,
            _ => Math.Min(Math.Ceiling(d.Height), 480),
        };

        var width = Math.Ceiling(d.Width);
        if (scene.Shape == SurfaceShape.Pill && width > DockedMaxWidth)
        {
            // Docked in a narrow gap: keep the start of the pill and fade out what doesn't fit.
            width = Math.Floor(DockedMaxWidth);
            ContentClip.OpacityMask = OverflowFade;
        }
        else
        {
            ContentClip.OpacityMask = null;
        }

        return new Size(width, height);
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
        AnimateSize(target, growing: true, Motion.Reduced, lively: false);
    }

    private void ApplyShape(SurfaceShape shape, bool reduced)
    {
        var resting = shape == SurfaceShape.Resting;

        // Hanging from the top edge: no top border (it meets the screen edge).
        var flushTop = _flushTop;
        SurfaceFill.BorderThickness = flushTop ? new Thickness(1, 0, 1, 1) : new Thickness(1);
        AttentionEdge.BorderThickness = flushTop ? new Thickness(1.5, 0, 1.5, 1.5) : new Thickness(1.5);
        PulseRing.BorderThickness = flushTop ? new Thickness(2, 0, 2, 2) : new Thickness(2);
        PulseRing.RenderTransformOrigin = flushTop ? new Point(0.5, 0) : new Point(0.5, 0.5);
        UpdateCorners();
        // A faint, translucent edge like Windows' own flyouts; the blurred backdrop does the rest.
        SurfaceFill.SetResourceReference(Border.BorderBrushProperty, "Brush.StrokeAcrylic");

        // The resting line is small, so its hit area is much larger than what is drawn: wide, tall,
        // and reaching all the way down to the taskbar edge so there is no dead gap below it.
        // The resting line and the pill share one generous hover target that reaches all the way to the
        // screen edge they sit against (the taskbar, or the top of the screen), so there is no dead gap
        // between them. It must not shrink out from under the pointer when hovering turns the line into
        // the peek pill, or the two flip back and forth. Against a side of the screen (or the start of a
        // taskbar gap) it stops at that edge.
        var docked = _docked;
        var compact = shape is SurfaceShape.Resting or SurfaceShape.Pill;
        var side = resting ? RestingReachSide : 0;
        var away = resting ? RestingReachAway : PillReachAway;
        var padLeft = _align == HAlign.Left ? 0 : side;
        var padRight = _align == HAlign.Right ? 0 : side;
        var edgeGap = _flushTop ? 0 : StageGap;
        HitArea.Background = compact ? HitBrush : Brushes.Transparent;
        HitArea.Padding = !compact ? new Thickness(0)
            : _anchorBottom ? new Thickness(padLeft, away, padRight, edgeGap)
            : new Thickness(padLeft, edgeGap, padRight, away);
        HitArea.Margin = !compact ? new Thickness(0)
            : _anchorBottom ? new Thickness(0, 0, 0, -edgeGap)
            : new Thickness(0, -edgeGap, 0, 0);
        HitArea.Cursor = resting ? Cursors.Hand : null;

        // Docked, the island is laid out for the 32 px pill centered on the taskbar; lift the thinner
        // resting line so it is centered too.
        HitArea.RenderTransform = docked && resting ? new TranslateTransform(0, -(32 - RestingSize.Height) / 2) : Transform.Identity;

        // Fluent Shadow 08 under the compact island, Shadow 28 under the expanded panel.
        // Docked in the taskbar, the compact island sits flat on it with no shadow.
        var large = shape is SurfaceShape.Panel or SurfaceShape.Banner;
        var strength = large ? 0.44 : docked ? 0 : 0.28;
        if (reduced)
        {
            Shadow.BeginAnimation(SoftShadow.BlurProperty, null);
            Shadow.BeginAnimation(SoftShadow.OffsetYProperty, null);
            Shadow.BeginAnimation(SoftShadow.StrengthProperty, null);
            Shadow.Blur = large ? 28 : 8;
            Shadow.OffsetY = large ? 14 : 4;
            Shadow.Strength = strength;
            return;
        }

        Shadow.BeginAnimation(SoftShadow.BlurProperty, Motion.To(large ? 28 : 8, Motion.Expand));
        Shadow.BeginAnimation(SoftShadow.OffsetYProperty, Motion.To(large ? 14 : 4, Motion.Expand));
        Shadow.BeginAnimation(SoftShadow.StrengthProperty, Motion.To(strength, Motion.Expand));
    }

    /// <summary>
    /// Fluent's overlay radius: 8 px on the pill, banner and panels (Windows 11 geometry). Only shapes
    /// shorter than 16 px (the 10 px resting line) use half their height. The radius follows the live
    /// height, so morphing from the line to the pill never jumps.
    /// </summary>
    private void UpdateCorners()
    {
        var h = Surface.ActualHeight > 0 ? Surface.ActualHeight : Surface.Height;
        var radius = Math.Max(2, Math.Min(h / 2, PanelRadius));
        _clipRadius = radius;

        var flushTop = _flushTop;
        var corners = flushTop ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(radius);
        SurfaceFill.CornerRadius = corners;
        AttentionEdge.CornerRadius = corners;
        PulseRing.CornerRadius = flushTop ? new CornerRadius(0, 0, radius + 2, radius + 2) : new CornerRadius(radius + 2);
        Shadow.Radius = radius;
        UpdateClip(ContentClip.RenderSize);
    }

    /// <summary>Fluent 2 overlay radius (radius/overlay token).</summary>
    private const double PanelRadius = 8;

    /// <summary>Final size of the running size animation, so the blur never trails outside the shape.</summary>
    private Size? _animTarget;

    private void AnimateSize(Size target, bool growing, bool reduced, bool lively = true)
    {
        var token = ++_sizeToken;
        _animTarget = reduced ? null : target;

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
        // Opening into something new is lively (a small springy overshoot, like the Dynamic Island);
        // following content that changed size on its own (a new song title) is calm, so it doesn't draw the eye.
        var duration = growing ? (lively ? Motion.Expand : TimeSpan.FromMilliseconds(260)) : Motion.Collapse;
        var ease = growing ? (lively ? Motion.Bouncy : Motion.Spring) : Motion.Standard;
        var delay = growing ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40);
        var height = Motion.To(target.Height, duration, ease, delay);
        height.Completed += (_, _) =>
        {
            if (token == _sizeToken)
            {
                _animTarget = null;
                SyncBackdrop();
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

            // New content blooms in: it grows from 96% to exactly full size from its centre as it fades
            // in. No overshoot, so text never spills past the island's edge.
            if (!reduced)
            {
                var bloom = new ScaleTransform(0.96, 0.96);
                next.RenderTransformOrigin = new Point(0.5, 0.5);
                next.RenderTransform = bloom;
                var grow = Motion.To(1, duration + TimeSpan.FromMilliseconds(80), Motion.Decelerate, delay);
                bloom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                bloom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
            else
            {
                next.RenderTransform = Transform.Identity;
            }
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

    private void UpdateClip(Size size)
    {
        if (!_flushTop || size.Width <= 0 || size.Height <= 0)
        {
            ContentClip.Clip = new RectangleGeometry(new Rect(size), _clipRadius, _clipRadius);
            return;
        }

        // Flush with the top edge: only the bottom corners are rounded.
        var r = Math.Min(_clipRadius, Math.Min(size.Width, size.Height) / 2);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 0), true, true);
            ctx.LineTo(new Point(size.Width, 0), false, false);
            ctx.LineTo(new Point(size.Width, size.Height - r), false, false);
            ctx.ArcTo(new Point(size.Width - r, size.Height), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r, size.Height), false, false);
            ctx.ArcTo(new Point(0, size.Height - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
        }

        g.Freeze();
        ContentClip.Clip = g;
    }

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
