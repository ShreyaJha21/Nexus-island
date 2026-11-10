using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Positioning;
using Nexus.Core.Settings;
using Nexus.Modes;
using Nexus.Modes.Agent;
using Nexus.Modes.Controls;
using Nexus.Modes.Focus;
using Nexus.Shell.Native;
using Nexus.Shell.Views;

namespace Nexus.Shell.Island;

/// <summary>
/// The island's brain. Arranges the modes' activities with the priority engine, then decides
/// whether the island rests, peeks, shows a pill, a banner or a panel, and handles every
/// interaction: hover, click, scroll, right-click, Esc, click-away, 8 s idle, moments, hiding.
/// </summary>
public sealed class IslandController
{
    private enum State { Collapsed, Peek, Expanded, Banner, Toast }
    private enum PanelKind { Mode, Launcher, Why }

    private static readonly TimeSpan PeekDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PanelIdle = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MomentHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BannerHold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AttentionHold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ToastHold = TimeSpan.FromSeconds(2);

    private readonly IslandWindow _window;
    private readonly IReadOnlyList<IslandMode> _modes;
    private readonly FocusMode _focus;
    private readonly NexusSettings _settings;
    private readonly Action _save;
    private readonly Dictionary<IslandMode, CompactFrame> _frames = new();
    private readonly Dictionary<string, DateTimeOffset> _paused = new();

    private readonly IdleViewModel _idle;
    private readonly WhyViewModel _whyVm;
    private readonly PeekView _peek;
    private readonly LauncherView _launcher;
    private readonly ToastView _toast;
    private readonly WhyView _why;

    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _pauseTimer;
    private readonly DispatcherTimer _hiddenUntilTimer;

    private IslandLayout _layout = IslandLayout.Empty;
    private State _state = State.Collapsed;
    private PanelKind _panelKind;
    private IslandMode? _panelMode;
    private IslandMode? _bannerMode;
    private IslandMode? _whyMode;
    private bool _userExpanded;
    private string? _selected;
    private IslandMode? _notifiedPanel;
    private bool _launcherOpen;

    private bool _visible;
    private bool _hiddenByUser;
    private bool _hiddenByFullscreen;

    public IslandController(
        IslandWindow window,
        IReadOnlyList<IslandMode> modes,
        NexusSettings settings,
        Action save,
        Func<bool> agentConnected,
        Action openAgentSetup,
        Action openSettingsFile)
    {
        _window = window;
        _modes = modes;
        _focus = modes.OfType<FocusMode>().Single();
        var agent = modes.OfType<AgentMode>().Single();
        _settings = settings;
        _save = save;
        OpenSettingsFile = openSettingsFile;

        _idle = new IdleViewModel(
            startFocus: () => { _focus.Start(_focus.DefaultPreset); Collapse(); },
            setupAgent: () => { Collapse(); openAgentSetup(); },
            recent: () => agent.LastFinishedText is { } text && agent.LastFinishedAt is { } at ? (text, at) : null,
            agentConnected: agentConnected)
        {
            FocusHint = $"{_focus.DefaultPreset.FocusMinutes} min",
        };
        _whyVm = new WhyViewModel(
            pause: () => { if (_whyMode is not null) PauseMode(_whyMode); Collapse(); },
            close: Collapse);

        _peek = new PeekView { DataContext = _idle };
        _launcher = new LauncherView { DataContext = _idle };
        _toast = new ToastView();
        _why = new WhyView { DataContext = _whyVm };

        _peekTimer = Timer(PeekDelay, OnPeekTimer);
        _idleTimer = Timer(PanelIdle, OnPanelIdle);
        _holdTimer = Timer(MomentHold, OnHoldEnded);
        _toastTimer = Timer(ToastHold, OnToastEnded);
        _pauseTimer = Timer(TimeSpan.FromSeconds(30), () => { if (ExpirePauses()) Rearrange(); });
        _hiddenUntilTimer = Timer(TimeSpan.FromMinutes(1), () => { CheckHiddenUntil(); RefreshQuiet(); });

        foreach (var mode in modes)
        {
            mode.ActivityChanged += (_, _) => Rearrange();
            mode.MomentRequested += (_, kind) => OnMoment(mode, kind);
            mode.CollapseRequested += (_, _) =>
            {
                if (_state == State.Expanded && _panelKind == PanelKind.Mode && _panelMode == mode)
                {
                    Collapse();
                }
            };
        }

        _window.SurfaceClicked += OnClick;
        _window.SurfaceRightClicked += OnRightClick;
        _window.SurfaceWheel += OnWheel;
        _window.BubbleClicked += OnBubbleClick;
        _window.PointerEntered += OnPointerEntered;
        _window.PointerExited += OnPointerExited;
        _window.PointerMoved += () => { if (_state == State.Expanded) _idleTimer.Restart(); };
        _window.EscapePressed += Collapse;
        _window.DragStarted += OnDragStarted;
        _window.DragEnded += OnDragEnded;
        _window.KeyboardDeactivated += () =>
        {
            // Click outside closes a panel the user opened (but not when focus moved to our own menu).
            if (_state == State.Expanded && _userExpanded && !_menuOpen)
            {
                Collapse();
            }
        };
    }

    public Action OpenSettingsFile { get; }

    private string? _hideShortcut = "Ctrl+Alt+Space";

    /// <summary>The working hide shortcut, or null when another app owns it.</summary>
    public void SetHideShortcut(string? text)
    {
        _hideShortcut = text;
        _toast.Hint.Text = text is null ? "Show it from the tray icon" : $"{text} to show";
        _idle.HideHint = text is null
            ? "Hide Nexus from the tray icon  ·  scroll on the island to switch modes"
            : $"{text} hides Nexus  ·  scroll on the island to switch modes";
    }

    public bool IsHiddenByUser => _hiddenByUser;

    // ================================================================== Startup and visibility

    public void Start()
    {
        _hiddenByUser = _settings.Hidden && (_settings.HiddenUntil is null || _settings.HiddenUntil > DateTimeOffset.Now);
        if (_settings.Hidden && !_hiddenByUser)
        {
            _settings.Hidden = false;
            _settings.HiddenUntil = null;
            _save();
        }

        _hiddenUntilTimer.Start();
        _pauseTimer.Start();
        Rearrange();
        UpdateVisibility();
    }

    /// <summary>F6. Ctrl+Alt+Space: gone instantly, back just as fast.</summary>
    public void ToggleHidden()
    {
        if (_hiddenByUser)
        {
            ShowIsland();
        }
        else
        {
            Hide(until: null);
        }
    }

    public void Hide(DateTimeOffset? until)
    {
        _hiddenByUser = true;
        _settings.Hidden = true;
        _settings.HiddenUntil = until;
        _save();

        if (!_visible)
        {
            return;
        }

        // A 2 s toast confirms, then the island sinks into the taskbar.
        EndExpansion();
        _state = State.Toast;
        Render();
        _toastTimer.Restart();
    }

    public void ShowIsland()
    {
        _hiddenByUser = false;
        _settings.Hidden = false;
        _settings.HiddenUntil = null;
        _save();
        UpdateVisibility();
    }

    public void SetFullscreen(bool fullscreen)
    {
        _hiddenByFullscreen = fullscreen;
        UpdateVisibility();
    }

    /// <summary>Tray: left click shows the island and opens the launcher (or the current mode).</summary>
    public void OpenFromTray()
    {
        if (_hiddenByUser)
        {
            ShowIsland();
        }

        if (!_visible && _hiddenByApp && !_hiddenByFullscreen)
        {
            // Hidden to keep out of an app's way: the user asked for it, so it comes out until it closes again.
            _showForAttention = true;
            UpdateVisibility();
        }

        if (_visible)
        {
            OnClick();
        }
    }

    public void Reposition() => _window.Reposition();

    // ================================================================== Smart positioning
    //
    // The island has a preferred spot (bottom centre unless the user chose or dragged otherwise).
    // What happens when the app in front has controls there depends on the mode:
    //   Fixed     it stays put.
    //   Smart     it glides to the first clear spot (app rule, then SafeSpots) and comes back when
    //             the conflict is gone. Nowhere clear: it stays, but small.
    //   AutoHide  it hides while a window covers its spot.
    // "Clear" comes from three sources, in this order: the window's bounds (not under the island at
    // all), per-app rules (Figma keeps its toolbar at the bottom centre), and UI Automation.

    private DispatcherTimer? _placeTimer;
    private DispatcherTimer? _recheckTimer;
    private ForegroundInfo? _foreground;
    private string? _foregroundRule;
    private int _placeRequest;
    private bool _hiddenByApp;
    private bool _showForAttention;
    private bool _appCompact;
    private bool _squeezed;
    private bool _revealed;
    private bool _lastQuiet;
    private IntPtr _placedByUserFor;
    private IntPtr _warmedUp;

    /// <summary>The watcher reports which app is in front and where its window is (null: the desktop).</summary>
    internal void SetForeground(ForegroundInfo? info)
    {
        var rule = info is null ? null : SmartPlacement.FindRuleKey(_settings.Positioning.Apps, info.Process, info.Title);
        var changed = info?.Hwnd != _foreground?.Hwnd || !Equals(info?.Bounds, _foreground?.Bounds) || rule != _foregroundRule;
        _foreground = info;
        _foregroundRule = rule;

        if (!changed)
        {
            return; // only the title changed (a new tab, a spinner): nothing to decide again
        }

        if (info?.Hwnd != _placedByUserFor)
        {
            _placedByUserFor = IntPtr.Zero;
        }

        DebugLog.Write(info is null ? "In front: desktop" : $"In front: {info.Process} {info.Bounds.Left},{info.Bounds.Top} {info.Bounds.Width}x{info.Bounds.Height} rule={rule ?? "none"}");
        SchedulePlacement();
    }

    /// <summary>Another window came to the front; the taskbar's buttons may have moved.</summary>
    public void OnForegroundChanged()
    {
        if (_window.IsDocked)
        {
            SchedulePlacement();
        }
    }

    /// <summary>Debounced, so flicking through windows with Alt+Tab doesn't bounce the island.</summary>
    private void SchedulePlacement()
    {
        _placeTimer ??= Timer(TimeSpan.FromMilliseconds(350), ApplyPlacement);
        _placeTimer.Restart();
    }

    private void ApplyPlacement()
    {
        _placeTimer?.Stop();

        // Never move the island while the user is looking at a panel, a banner or the hide toast, or
        // holding it; Collapse, the end of a banner and the drop call this again.
        if (_state is State.Expanded or State.Banner or State.Toast || _window.IsDragging)
        {
            return;
        }

        var pos = _settings.Positioning;
        var preferred = _settings.Placement;
        var fg = _foreground;
        var request = ++_placeRequest;
        var rule = fg is not null && pos.Mode == PositionMode.Smart ? SmartPlacement.FindRule(pos.Apps, fg.Process, fg.Title) : null;
        _appCompact = rule?.Compact == true;

        if (pos.Mode == PositionMode.AutoHide)
        {
            var covered = fg is not null && _window.Footprint(preferred) is { } spot && fg.Bounds.Intersects(spot);
            Settle(request, preferred, moved: null, hide: covered, squeezed: false);
            return;
        }

        if (pos.Mode == PositionMode.Fixed || fg is null || fg.Hwnd == _placedByUserFor)
        {
            Settle(request, preferred, moved: null, hide: false, squeezed: false);
            return;
        }

        if (rule is null && pos.StayInBrowsers && SmartPlacement.IsBrowser(fg.Process))
        {
            DebugLog.Write($"Smart position for {fg.Process}: a browser, staying put");
            Settle(request, preferred, moved: null, hide: false, squeezed: false);
            return;
        }

        if (rule?.Hide == true)
        {
            Settle(request, preferred, moved: null, hide: true, squeezed: false);
            return;
        }

        var candidates = new List<Placement>();
        if (rule?.MoveTo is { } first)
        {
            candidates.Add(first);
        }

        candidates.AddRange(pos.SafeSpots);

        var footprints = new Dictionary<Placement, Win32.RECT>();
        foreach (var spot in candidates.Prepend(preferred).Distinct())
        {
            if (_window.Footprint(spot) is { } area)
            {
                footprints[spot] = area;
            }
        }

        var detect = pos.DetectControls;
        var scale = _window.ScaleFactor;
        var minGap = MinGapWidth;

        // Reading another app's controls and the taskbar takes a few hundred milliseconds: off the UI thread.
        Task.Run(() =>
        {
            TaskbarGap? gap = null;
            var gapKnown = false;
            var probed = false;

            bool Blocked(Placement spot)
            {
                if (spot == Placement.Taskbar)
                {
                    if (!gapKnown)
                    {
                        gapKnown = true;
                        gap = BestGap(minGap);
                    }

                    return gap is null; // no room in the taskbar
                }

                if (!footprints.TryGetValue(spot, out var area))
                {
                    return true; // not possible on this setup
                }

                if (!fg.Bounds.Intersects(area))
                {
                    return false; // the window doesn't reach this spot
                }

                if (rule is { Avoid.Count: > 0 })
                {
                    return rule.Avoid.Contains(spot); // a known app: its rule is the whole truth
                }

                if (!detect)
                {
                    return false;
                }

                probed = true;
                var blocked = ControlProbe.HasControls(fg.Hwnd, area, scale, out var found);
                DebugLog.Write($"{spot}: {(blocked ? $"taken by {found}" : "clear")}");
                return blocked;
            }

            var chosen = SmartPlacement.Choose(preferred, candidates, Blocked);
            return (Chosen: chosen, Gap: gap, GapKnown: gapKnown, Probed: probed);
        }).ContinueWith(t =>
        {
            if (request != _placeRequest || t.IsFaulted)
            {
                return;
            }

            var result = t.Result;
            DebugLog.Write($"Smart position for {fg.Process}: {result.Chosen?.ToString() ?? "nowhere clear, staying small"}");
            Settle(request, preferred, moved: result.Chosen == preferred ? null : result.Chosen,
                hide: false, squeezed: result.Chosen is null, result.Gap, result.GapKnown);

            // Chromium apps only describe their controls a moment after first being asked, and a
            // just-opened window may still be loading: look once more, a little later.
            if (result.Probed && _warmedUp != fg.Hwnd)
            {
                _warmedUp = fg.Hwnd;
                _recheckTimer ??= Timer(TimeSpan.FromMilliseconds(1200), () =>
                {
                    _recheckTimer!.Stop();
                    ApplyPlacement();
                });
                _recheckTimer.Restart();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Carries out a decision: where the island is, whether it is hidden, whether it stays small.</summary>
    private void Settle(int request, Placement preferred, Placement? moved, bool hide, bool squeezed, TaskbarGap? gap = null, bool gapKnown = false)
    {
        if (request != _placeRequest || _state is State.Expanded or State.Banner or State.Toast || _window.IsDragging)
        {
            return;
        }

        // The taskbar spot needs to know where the free space is.
        if ((moved ?? preferred) == Placement.Taskbar && !gapKnown)
        {
            var minGap = MinGapWidth;
            Task.Run(() => BestGap(minGap)).ContinueWith(
                t => Settle(request, preferred, moved, hide, squeezed, t.IsFaulted ? null : t.Result, gapKnown: true),
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        _squeezed = squeezed;
        if (_hiddenByApp != hide)
        {
            _hiddenByApp = hide;
            UpdateVisibility();
        }

        _window.MoveTo(preferred, moved, gap);
        RefreshQuiet();
    }

    private int MinGapWidth => (int)Math.Round(100 * _window.ScaleFactor);

    /// <summary>The widest free stretch of the taskbar; on a tie, the leftmost (where Windows puts the weather widget).</summary>
    private static TaskbarGap? BestGap(int minWidth) =>
        TaskbarLayout.FindGaps(minWidth)?.OrderByDescending(g => g.Width).ThenBy(g => g.Left).Cast<TaskbarGap?>().FirstOrDefault();

    // ------------------------------------------------------------------ Staying compact

    /// <summary>
    /// Compact: activities show as the thin line and nothing opens on its own, except "needs you".
    /// Pointing at the line shows the pill; clicking opens the panel as usual.
    /// </summary>
    private bool Quiet =>
        _settings.Positioning.AlwaysCompact || _appCompact || _squeezed ||
        SmartPlacement.IsCompactTime(_settings.Positioning.CompactHours, TimeOnly.FromDateTime(DateTime.Now));

    private void RefreshQuiet()
    {
        var quiet = Quiet;
        if (quiet != _lastQuiet)
        {
            _lastQuiet = quiet;
            DebugLog.Write($"Compact {quiet}");
            Render();
        }
    }

    // ------------------------------------------------------------------ The user's choices

    public void SetPlacement(Placement placement)
    {
        _settings.Placement = placement;
        _save();
        _placedByUserFor = IntPtr.Zero;
        ApplyPlacement();
    }

    public void SetMode(PositionMode mode)
    {
        _settings.Positioning.Mode = mode;
        _save();
        _placedByUserFor = IntPtr.Zero;
        ApplyPlacement();
    }

    public void SetDraggable(bool on)
    {
        _settings.Positioning.Draggable = on;
        _save();
        Render();
    }

    public void SetAlwaysCompact(bool on)
    {
        _settings.Positioning.AlwaysCompact = on;
        _save();
        RefreshQuiet();
    }

    private void OnDragStarted()
    {
        _peekTimer.Stop();
        _placeRequest++; // a decision still on its way must not pull the island out of the user's hand
    }

    /// <summary>The user dropped the island: that is now its preferred spot, and it stays there in this app.</summary>
    private void OnDragEnded(Placement placement, double x, double y, string? monitor)
    {
        _settings.Placement = placement;
        if (placement == Placement.Custom)
        {
            _settings.Positioning.CustomX = x;
            _settings.Positioning.CustomY = y;
            _settings.Positioning.CustomMonitor = monitor;
            _window.CustomX = x;
            _window.CustomY = y;
            _window.CustomMonitor = monitor;
        }

        _save();
        _placedByUserFor = _foreground?.Hwnd ?? IntPtr.Zero;
        _squeezed = false;
        _placeRequest++;
        _window.MoveTo(placement, moved: null, gap: null);
        RefreshQuiet();
    }

    /// <summary>The "Position" menu: where the island lives and how it keeps out of the way.</summary>
    public void OpenPositionMenu()
    {
        var pos = _settings.Positioning;
        var menu = new ContextMenu();

        void Spot(string header, Placement placement) =>
            menu.Items.Add(Item(header, _settings.Placement == placement ? "check" : "", () => SetPlacement(placement)));

        Spot("Bottom centre", Placement.AboveTaskbar);
        Spot("Bottom left", Placement.BottomLeft);
        Spot("Bottom right", Placement.BottomRight);
        Spot("Top centre", Placement.TopCenter);
        Spot("Inside the taskbar", Placement.Taskbar);
        if (_settings.Placement == Placement.Custom)
        {
            menu.Items.Add(Item("Where you dragged it", "check", () => { }));
        }

        menu.Items.Add(new Separator());

        void Mode(string header, PositionMode mode) =>
            menu.Items.Add(Item(header, pos.Mode == mode ? "check" : "", () => SetMode(mode)));

        Mode("Move out of the way of apps", PositionMode.Smart);
        Mode("Hide when an app covers it", PositionMode.AutoHide);
        Mode("Always stay put", PositionMode.Fixed);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Drag to move", pos.Draggable ? "check" : "", () => SetDraggable(!pos.Draggable)));
        menu.Items.Add(Item("Stay put in browsers", pos.StayInBrowsers ? "check" : "", () =>
        {
            pos.StayInBrowsers = !pos.StayInBrowsers;
            _save();
            ApplyPlacement();
        }));
        menu.Items.Add(Item("Always compact", pos.AlwaysCompact ? "check" : "", () => SetAlwaysCompact(!pos.AlwaysCompact)));

        OpenMenu(menu, null);
    }

    /// <summary>Hidden by the user or a fullscreen app always wins; hidden for an app gives way to "needs you".</summary>
    private bool ShouldShow => !_hiddenByUser && !_hiddenByFullscreen && (!_hiddenByApp || _showForAttention);

    private void UpdateVisibility()
    {
        var shouldShow = ShouldShow;
        if (shouldShow && !_visible)
        {
            _visible = true;
            _state = State.Collapsed;
            Render();
            _window.Rise();
        }
        else if (!shouldShow && _visible && _state != State.Toast)
        {
            EndExpansion();
            _visible = false;
            _window.Sink();
        }
    }

    private void OnToastEnded()
    {
        _toastTimer.Stop();
        _state = State.Collapsed;
        if (!ShouldShow)
        {
            _visible = false;
            _window.Sink(Render);
        }
        else
        {
            Render();
        }

        SchedulePlacement();
    }

    private void CheckHiddenUntil()
    {
        if (_hiddenByUser && _settings.HiddenUntil is { } until && until <= DateTimeOffset.Now)
        {
            ShowIsland();
        }
    }

    // ================================================================== Arranging

    private void Rearrange()
    {
        ExpirePauses();
        var options = new ArrangeOptions(_selected, _settings.PinnedModeId, _paused.Keys.ToHashSet());
        _layout = PriorityEngine.Arrange(_modes.Select(m => m.Activity).OfType<Activity>(), options);

        if (_selected is not null && _layout.Visible.All(a => a.ModeId != _selected))
        {
            _selected = null;
        }

        // Focus Guard: everything waiting behind the session shows as a dot on the focus pill.
        _focus.SetGuarded(_layout.Guarded.Select(a => Mode(a.ModeId)?.Name ?? a.ModeId).ToList());

        switch (_state)
        {
            case State.Expanded when _panelKind == PanelKind.Mode && _panelMode?.Activity is null:
            case State.Expanded when _panelKind == PanelKind.Why && _whyMode?.Activity is null:
                Collapse();
                return;
            case State.Banner when _bannerMode?.Activity is null:
                EndBanner();
                break;
            case State.Peek when _layout.Primary is not null:
                _state = State.Collapsed;
                break;
        }

        Render();
    }

    private bool _rendering;
    private bool _renderPending;

    /// <summary>
    /// Renders the current state. Opening a panel notifies its mode, which may update state and ask
    /// for another render; those nested requests are coalesced so a stale scene never wins.
    /// </summary>
    private void Render()
    {
        if (_rendering)
        {
            _renderPending = true;
            return;
        }

        _rendering = true;
        try
        {
            var passes = 0;
            do
            {
                _renderPending = false;
                RenderOnce();
            }
            while (_renderPending && ++passes < 4);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderOnce()
    {
        _window.CanDrag = _visible && _settings.Positioning.Draggable && _state is State.Collapsed or State.Peek;
        if (!_visible)
        {
            NotifyPanel(null);
            return;
        }

        // Tell modes about panel changes first: they may refresh their state, which must be in the scene.
        NotifyPanel(_state == State.Expanded && _panelKind == PanelKind.Mode ? _panelMode : null);
        SetLauncherOpen(_state == State.Expanded && _panelKind == PanelKind.Launcher);
        if (_renderPending)
        {
            return; // state changed while notifying; the loop renders again with fresh state
        }

        var primary = PrimaryMode;
        Scene scene;

        switch (_state)
        {
            case State.Toast:
                scene = new Scene(SurfaceShape.Pill, _toast);
                break;

            case State.Expanded:
                FrameworkElement view = _panelKind switch
                {
                    PanelKind.Launcher => _launcher,
                    PanelKind.Why => _why,
                    _ => _panelMode!.PanelView,
                };
                scene = new Scene(SurfaceShape.Panel, view, Attention: _panelKind == PanelKind.Mode && _panelMode!.HasAttentionEdge,
                    AttentionBrush: _panelKind == PanelKind.Mode ? _panelMode!.AttentionBrush : null);
                break;

            case State.Banner when _bannerMode?.BannerView is { } banner:
                scene = new Scene(SurfaceShape.Banner, banner);
                break;

            case State.Peek:
                scene = new Scene(SurfaceShape.Pill, _peek);
                break;

            default:
                if (primary is null)
                {
                    scene = new Scene(SurfaceShape.Resting);
                }
                else if (Quiet && !_revealed && primary.Activity?.Priority.IsUrgent() != true)
                {
                    // Compact: the activity is there, but only as the line, until the pointer asks for more.
                    scene = new Scene(SurfaceShape.Resting);
                }
                else
                {
                    var frame = Frame(primary);
                    frame.Overflow = _layout.OverflowCount;
                    var secondary = _layout.Secondary is { } s ? Mode(s.ModeId) : null;
                    scene = new Scene(SurfaceShape.Pill, frame, primary.HasAttentionEdge, secondary?.BubbleView, primary.AttentionBrush);
                }

                break;
        }

        _window.Present(scene);
    }

    // ================================================================== Expanding and collapsing

    private void Expand(PanelKind kind, IslandMode? mode, bool byUser, MomentKind? moment = null)
    {
        _peekTimer.Stop();
        _state = State.Expanded;
        _panelKind = kind;
        _panelMode = kind == PanelKind.Mode ? mode : null;
        _bannerMode = null;

        if (byUser)
        {
            _userExpanded = true;
            _holdTimer.Stop();
            _idleTimer.Restart();
            _window.EnableKeyboard();
        }
        else if (!_userExpanded)
        {
            _holdTimer.Interval = moment == MomentKind.Attention ? AttentionHold : MomentHold;
            _holdTimer.Restart();
        }

        Render();
        if (moment == MomentKind.Attention)
        {
            _window.Pulse();
        }
    }

    private void Collapse()
    {
        if (_state is State.Toast)
        {
            return;
        }

        EndExpansion();
        _state = State.Collapsed;
        if (_showForAttention)
        {
            // It only came out for "needs you"; the app that hides it is still in front.
            _showForAttention = false;
            UpdateVisibility();
        }

        Render();
        SchedulePlacement(); // a move may have waited for the panel to close
    }

    private void EndExpansion()
    {
        var banner = _bannerMode;
        _holdTimer.Stop();
        _idleTimer.Stop();
        _peekTimer.Stop();
        _userExpanded = false;
        _panelMode = null;
        _bannerMode = null;
        _window.DisableKeyboard();
        banner?.OnMomentEnded(MomentKind.Banner);
    }

    private void EndBanner()
    {
        var banner = _bannerMode;
        _bannerMode = null;
        _holdTimer.Stop();
        _state = State.Collapsed;
        banner?.OnMomentEnded(MomentKind.Banner);
    }

    private void OnHoldEnded()
    {
        _holdTimer.Stop();
        if (_window.IsPointerOver)
        {
            // The user is looking at it: treat it as theirs, and let the 8 s idle rule close it.
            _userExpanded = _state == State.Expanded;
            _idleTimer.Restart();
            return;
        }

        if (_state == State.Banner)
        {
            EndBanner();
            Render();
            SchedulePlacement();
        }
        else
        {
            Collapse();
        }
    }

    private void OnPanelIdle()
    {
        if (_window.IsPointerOver)
        {
            _idleTimer.Restart();
            return;
        }

        if (_state is State.Expanded or State.Banner)
        {
            Collapse();
        }
    }

    // ================================================================== Moments

    private void OnMoment(IslandMode mode, MomentKind kind)
    {
        if (!_visible && kind == MomentKind.Attention && _hiddenByApp && !_hiddenByUser && !_hiddenByFullscreen)
        {
            // Hidden only to keep out of an app's way: "needs you" is essential, so come out for it.
            _showForAttention = true;
            UpdateVisibility();
        }

        if (!_visible || _state == State.Toast)
        {
            return; // Hidden: state is kept and shows when the island comes back.
        }

        if (kind == MomentKind.Attention)
        {
            // "Agent needs you" always expands, even during focus or over another panel.
            Expand(PanelKind.Mode, mode, byUser: false, MomentKind.Attention);
            return;
        }

        if (Quiet)
        {
            return; // Compact: nothing but "needs you" opens on its own.
        }

        if (_state == State.Expanded && _userExpanded)
        {
            return; // Never interrupt something the user opened.
        }

        if (_layout.Primary?.ModeId != mode.Id)
        {
            return; // Outranked, or waiting behind Focus Guard.
        }

        if (kind == MomentKind.Banner && mode.BannerView is not null)
        {
            _state = State.Banner;
            _bannerMode = mode;
            _holdTimer.Interval = BannerHold;
            _holdTimer.Restart();
            Render();
            return;
        }

        Expand(PanelKind.Mode, mode, byUser: false, MomentKind.Panel);
    }

    // ================================================================== Pointer and keyboard

    private void OnPointerEntered()
    {
        if (_state == State.Collapsed && _visible && !_window.IsDragging && (_layout.Primary is null || (Quiet && !_revealed)))
        {
            _peekTimer.Restart();
        }

        if (_state == State.Expanded)
        {
            _idleTimer.Restart();
        }
    }

    private void OnPointerExited()
    {
        _peekTimer.Stop();
        if (_window.IsDragging)
        {
            return;
        }

        if (_revealed)
        {
            _revealed = false;
            Render();
        }

        if (_state == State.Peek)
        {
            _state = State.Collapsed;
            Render();
        }
    }

    private void OnPeekTimer()
    {
        _peekTimer.Stop();
        if (_state != State.Collapsed || !_window.IsPointerOver || _window.IsDragging)
        {
            return;
        }

        if (_layout.Primary is null)
        {
            _idle.Refresh();
            _state = State.Peek;
            Render();
        }
        else if (Quiet && !_revealed)
        {
            _revealed = true; // compact: pointing at the line shows what is going on
            Render();
        }
    }

    private void OnClick()
    {
        switch (_state)
        {
            case State.Collapsed or State.Peek:
                if (PrimaryMode is { } primary)
                {
                    Expand(PanelKind.Mode, primary, byUser: true);
                }
                else
                {
                    Expand(PanelKind.Launcher, null, byUser: true);
                }

                break;

            case State.Banner when _bannerMode is { } banner:
                Expand(PanelKind.Mode, banner, byUser: true);
                break;

            case State.Expanded when !_userExpanded:
                _userExpanded = true;
                _holdTimer.Stop();
                _idleTimer.Restart();
                _window.EnableKeyboard();
                break;
        }
    }

    private void OnBubbleClick()
    {
        if (_layout.Secondary is { } secondary && Mode(secondary.ModeId) is { } mode)
        {
            Expand(PanelKind.Mode, mode, byUser: true);
        }
    }

    /// <summary>Scroll on the island cycles between active modes.</summary>
    private void OnWheel(int delta)
    {
        if (_layout.Visible.Count < 2 || _layout.Primary?.Priority.IsUrgent() == true)
        {
            return;
        }

        _selected = PriorityEngine.NextModeId(_layout, delta > 0 ? -1 : 1);
        Rearrange();

        if (_state == State.Expanded && _panelKind == PanelKind.Mode && PrimaryMode is { } primary)
        {
            _panelMode = primary;
            Render();
        }
    }

    // ================================================================== Right-click menu (Figma 21:851)

    private void OnRightClick()
    {
        DebugLog.Write("Right click");
        var primary = PrimaryMode;
        var menu = new ContextMenu();

        if (primary is not null)
        {
            var pinned = _settings.PinnedModeId == primary.Id;
            menu.Items.Add(Item(pinned ? "Unpin this mode" : "Pin this mode", "pin", () =>
            {
                _settings.PinnedModeId = pinned ? null : primary.Id;
                _save();
                Rearrange();
            }));
        }

        if (_layout.Visible.Count > 1)
        {
            menu.Items.Add(Item("Show next mode", "chevronDown", () => OnWheel(-1), gesture: "Scroll"));
        }

        if (primary?.Activity is { } activity)
        {
            if (!activity.Priority.IsUrgent())
            {
                menu.Items.Add(Item($"Pause {primary.Name.ToLowerInvariant()} for 1 hour", "pause", () => PauseMode(primary)));
            }

            menu.Items.Add(Item("Why am I seeing this?", "info", () => ShowWhy(primary)));
        }

        menu.Items.Add(Item("Position…", "location", () => _window.Dispatcher.BeginInvoke(OpenPositionMenu, DispatcherPriority.ApplicationIdle)));
        menu.Items.Add(Item("Choose what shows…", "settings", OpenSettingsFile));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Hide Nexus", "eyeOff", () => Hide(until: null), gesture: _hideShortcut));

        // The user clicked the island, so it may take keyboard focus for the menu (Esc, arrows).
        _menuOpen = true;
        _window.EnableKeyboard();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_state != State.Expanded || !_userExpanded)
            {
                _window.DisableKeyboard();
            }
        };
        OpenMenu(menu, _window.SurfaceElement);
    }

    private bool _menuOpen;

    /// <summary>
    /// Opens a Fluent context menu at the pointer. The menu captures the mouse, so a click anywhere
    /// else closes it. The caller activates a window first if the menu should also take Esc and arrows.
    /// </summary>
    public static void OpenMenu(ContextMenu menu, UIElement? target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) => DebugLog.Write("Menu closed");
        menu.Opened += (_, _) => DebugLog.Write("Menu opened");
        menu.IsOpen = true;
    }

    public static MenuItem Item(string header, string icon, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            Icon = new NexusIcon { Kind = icon, Width = 16, Height = 16 },
            IsEnabled = enabled,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowWhy(IslandMode mode)
    {
        if (mode.Activity is not { } activity)
        {
            return;
        }

        _whyMode = mode;
        _whyVm.Show(mode.Name, activity.Reason, (int)activity.Priority, canPause: !activity.Priority.IsUrgent());
        Expand(PanelKind.Why, mode, byUser: true);
    }

    private void PauseMode(IslandMode mode)
    {
        _paused[mode.Id] = DateTimeOffset.UtcNow.AddHours(1);
        if (_selected == mode.Id)
        {
            _selected = null;
        }

        Rearrange();
    }

    private bool ExpirePauses()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _paused.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var id in expired)
        {
            _paused.Remove(id);
        }

        return expired.Count > 0;
    }

    // ================================================================== Helpers

    private IslandMode? PrimaryMode => _layout.Primary is { } p ? Mode(p.ModeId) : null;

    private IslandMode? Mode(string id) => _modes.FirstOrDefault(m => m.Id == id);

    private CompactFrame Frame(IslandMode mode)
    {
        if (!_frames.TryGetValue(mode, out var frame))
        {
            frame = new CompactFrame(mode.CompactView);
            _frames[mode] = frame;
        }

        return frame;
    }

    private void NotifyPanel(IslandMode? mode)
    {
        if (ReferenceEquals(mode, _notifiedPanel))
        {
            return;
        }

        _notifiedPanel?.OnPanelClosed();
        _notifiedPanel = mode;
        mode?.OnPanelOpened();
    }

    private void SetLauncherOpen(bool open)
    {
        if (open == _launcherOpen)
        {
            return;
        }

        _launcherOpen = open;
        if (open)
        {
            _idle.OnOpened();
        }
        else
        {
            _idle.OnClosed();
        }
    }

    private static DispatcherTimer Timer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }
}

internal static class DispatcherTimerExtensions
{
    public static void Restart(this DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
