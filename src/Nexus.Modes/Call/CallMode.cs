using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Calls;

namespace Nexus.Modes.Call;

/// <summary>What the island shows about a call.</summary>
public enum CallPhase { None, Incoming, Outgoing, Ongoing, Ended, Missed }

/// <summary>
/// WhatsApp calls (Figma: Island / Call). Mirrors what WhatsApp shows: a ringing call grows to the
/// 56 px banner with decline and accept; on the call the pill shows a green timer, the caller, a live
/// level meter, and mute and end buttons; afterwards "Call ended" for a moment, or "Missed call" until
/// you call back or dismiss it. Every button hands off to WhatsApp's own button.
/// </summary>
public sealed class CallMode : IslandMode
{
    private static readonly TimeSpan EndedHold = TimeSpan.FromSeconds(4);
    private const string UnknownCaller = "WhatsApp call";

    /// <summary>A call that rang this long without being answered counts as missed (declining is quicker).</summary>
    private static readonly TimeSpan MissedAfter = TimeSpan.FromSeconds(20);

    private static readonly (string From, string To)[] AvatarColors =
    [
        ("#FFF06292", "#FF8E24AA"),
        ("#FF26C6DA", "#FF1E88E5"),
        ("#FFFFB74D", "#FFF4511E"),
        ("#FF81C784", "#FF00897B"),
        ("#FF9575CD", "#FF3949AB"),
    ];

    private readonly Func<CallAction, Task<bool>> _perform;
    private readonly Action _showApp;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _endedTimer;

    private CallPhase _phase;
    private CallSnapshot? _call;
    private DateTimeOffset _ringingSince;
    private DateTimeOffset _connectedAt;
    private DateTimeOffset _missedAt;
    private TimeSpan _finalDuration;
    private bool _handledByUser;
    private bool _mutedLocally;
    private DateTimeOffset _muteChangedAt;

    /// <param name="perform">Presses WhatsApp's button for an action; false when it couldn't.</param>
    /// <param name="showApp">Brings WhatsApp's call window (or WhatsApp) to the front.</param>
    public CallMode(Func<CallAction, Task<bool>> perform, Action showApp)
    {
        _perform = perform;
        _showApp = showApp;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnPropertyChanged(nameof(DurationText));
        _endedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = EndedHold };
        _endedTimer.Tick += (_, _) =>
        {
            _endedTimer.Stop();
            if (_phase == CallPhase.Ended)
            {
                SetPhase(CallPhase.None);
            }
        };

        AcceptCommand = new RelayCommand(() => Hand(CallAction.Accept));
        DeclineCommand = new RelayCommand(() => Hand(CallAction.Decline));
        EndCommand = new RelayCommand(() => Hand(_phase == CallPhase.Incoming ? CallAction.Decline : CallAction.End));
        ToggleMuteCommand = new RelayCommand(() =>
        {
            var mute = !IsMuted;
            _mutedLocally = mute; // show it straight away; WhatsApp's own state follows a moment later
            _muteChangedAt = DateTimeOffset.UtcNow;
            Refresh();
            Hand(mute ? CallAction.Mute : CallAction.Unmute);
        });
        CallBackCommand = new RelayCommand(() =>
        {
            _showApp();
            SetPhase(CallPhase.None);
            RequestCollapse();
        });
        DismissCommand = new RelayCommand(() =>
        {
            SetPhase(CallPhase.None);
            RequestCollapse();
        });
        OpenAppCommand = new RelayCommand(_showApp);
    }

    public override string Id => "call";
    public override string Name => "Calls";

    public ICommand AcceptCommand { get; }
    public ICommand DeclineCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand CallBackCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand OpenAppCommand { get; }

    public CallPhase Phase => _phase;
    public bool IsIncoming => _phase == CallPhase.Incoming;
    public bool IsOutgoing => _phase == CallPhase.Outgoing;
    public bool IsOngoing => _phase == CallPhase.Ongoing;
    public bool IsEnded => _phase == CallPhase.Ended;
    public bool IsMissed => _phase == CallPhase.Missed;
    public bool IsLive => _phase is CallPhase.Ongoing or CallPhase.Outgoing;

    /// <summary>The live meter moves only while you can be heard.</summary>
    public bool ShowLevel => IsOngoing && !IsMuted;

    public bool IsVideo => _call?.IsVideo == true;

    public bool IsMuted =>
        DateTimeOffset.UtcNow - _muteChangedAt < TimeSpan.FromSeconds(3) ? _mutedLocally : _call?.IsMuted == true;

    public bool ShowMutedPill => IsOngoing && IsMuted;
    public bool ShowTalkingPill => IsOngoing && !IsMuted;

    public bool CanMute => IsOngoing && _call?.CanMute != false;
    public bool CanEnd => IsLive || IsIncoming;

    public string Caller { get; private set; } = "";
    public string ShortCaller => Caller.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Caller;
    public string Initials => CallReader.Initials(Caller);
    public Brush AvatarBrush { get; private set; } = Brushes.Gray;

    public string Kind => IsVideo ? "video call" : "voice call";
    public string AppLine => $"WhatsApp  ·  {Kind}";
    public string AcceptIcon => IsVideo ? "video" : "phone";
    public string CallIcon => IsVideo ? "video" : "phone";
    public string MuteIcon => IsMuted ? "micOff" : "mic";
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    public string DurationText => _phase switch
    {
        CallPhase.Ongoing => CallReader.FormatDuration(Elapsed),
        CallPhase.Ended => CallReader.FormatDuration(_finalDuration),
        _ => "",
    };

    public string MissedTime => _missedAt.ToLocalTime().ToString("h:mm tt");

    /// <summary>The panel's green status line.</summary>
    public string StatusLine => _phase switch
    {
        CallPhase.Incoming => $"Incoming {Kind}",
        CallPhase.Outgoing => "Calling…",
        CallPhase.Ongoing => $"WhatsApp {Kind}  ·  {DurationText}",
        CallPhase.Ended => $"Call ended  ·  {DurationText}",
        CallPhase.Missed => $"Missed {Kind}  ·  {MissedTime}",
        _ => "",
    };

    public Brush StatusBrush => Res(_phase switch
    {
        CallPhase.Ongoing => "Brush.Success",
        CallPhase.Missed => "Brush.Critical",
        _ => "Brush.TextSecondary",
    });

    /// <summary>The pill's icon: what state the call is in at a glance.</summary>
    public string CompactIcon => _phase switch
    {
        CallPhase.Ongoing when IsMuted => "micOff",
        CallPhase.Ended => "phoneEnd",
        _ => CallIcon,
    };

    public Brush CompactIconBrush => Res(_phase switch
    {
        CallPhase.Ongoing when IsMuted => "Brush.Critical",
        CallPhase.Missed => "Brush.Critical",
        CallPhase.Ended => "Brush.TextSecondary",
        _ => "Brush.Success",
    });

    /// <summary>A ringing call stays as the banner until it is answered, declined or missed.</summary>
    public override bool KeepsMoment => _phase == CallPhase.Incoming;

    protected override FrameworkElement CreateCompactView() => new CallCompactView();
    protected override FrameworkElement CreatePanelView() => new CallPanelView();
    protected override FrameworkElement? CreateBubbleView() => new CallBubbleView();
    protected override FrameworkElement? CreateBannerView() => new CallBannerView();

    private TimeSpan Elapsed
    {
        get
        {
            var t = DateTimeOffset.UtcNow - _connectedAt;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    /// <summary>The watcher saw a change. <paramref name="micSince"/> is when WhatsApp opened the microphone.</summary>
    public void Update(CallSnapshot? call, DateTimeOffset? micSince)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = _phase;
        if (call is not null)
        {
            _call = call;

            // A new call takes the name it shows. During a call, keep the name already known: a call
            // shown inside WhatsApp's main window sits among chat names that must not replace it.
            var newCall = previous is CallPhase.None or CallPhase.Ended or CallPhase.Missed;
            var known = Caller.Length > 0 && Caller != UnknownCaller;
            if (!string.IsNullOrWhiteSpace(call.Caller) && (newCall || !known))
            {
                SetCaller(call.Caller!);
            }
            else if (newCall && string.IsNullOrWhiteSpace(call.Caller))
            {
                SetCaller(UnknownCaller);
            }
        }

        switch (call?.State)
        {
            case CallState.Incoming:
                if (previous != CallPhase.Incoming)
                {
                    _ringingSince = now;
                    _handledByUser = false;
                    SetPhase(CallPhase.Incoming);
                    RequestMoment(MomentKind.Banner);
                    return;
                }

                break;

            case CallState.Outgoing:
                SetPhase(CallPhase.Outgoing);
                if (previous == CallPhase.Incoming)
                {
                    RequestCollapse();
                }

                return;

            case CallState.Ongoing:
                if (previous != CallPhase.Ongoing)
                {
                    // Prefer WhatsApp's own timer; otherwise the moment it opened the microphone.
                    _connectedAt = call.Duration is { } shown ? now - shown : micSince ?? now;
                    SetPhase(CallPhase.Ongoing);
                    if (previous == CallPhase.Incoming)
                    {
                        RequestCollapse(); // the ringing banner gives way to the call pill
                    }

                    return;
                }

                if (call.Duration is { } d && Math.Abs((Elapsed - d).TotalSeconds) > 2)
                {
                    _connectedAt = now - d;
                }

                break;

            case null:
                switch (previous)
                {
                    case CallPhase.Ongoing or CallPhase.Outgoing:
                        _finalDuration = previous == CallPhase.Ongoing ? Elapsed : TimeSpan.Zero;
                        SetPhase(previous == CallPhase.Ongoing ? CallPhase.Ended : CallPhase.None);
                        return;

                    case CallPhase.Incoming:
                        // Stopped ringing without being picked up here or there.
                        var missed = !_handledByUser && now - _ringingSince >= MissedAfter;
                        _missedAt = now;
                        SetPhase(missed ? CallPhase.Missed : CallPhase.None);
                        RequestCollapse();
                        return;
                }

                return;
        }

        Refresh();
    }

    private void SetPhase(CallPhase phase)
    {
        _phase = phase;
        if (phase == CallPhase.Ongoing)
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }

        _endedTimer.Stop();
        if (phase == CallPhase.Ended)
        {
            _endedTimer.Start();
        }

        if (phase == CallPhase.None)
        {
            _call = null;
            _mutedLocally = false;
            _muteChangedAt = default;
        }

        Refresh();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var since = Activity?.Since ?? DateTimeOffset.UtcNow;
        SetActivity(_phase switch
        {
            CallPhase.Incoming => new Activity(Id, Priority.CallRinging, _ringingSince)
            {
                BreaksFocus = true,
                Reason = $"{Caller} is calling you on WhatsApp. A ringing call outranks everything except an agent that needs you.",
            },
            CallPhase.Ongoing or CallPhase.Outgoing => new Activity(Id, Priority.CallOngoing, since)
            {
                BreaksFocus = true,
                Reason = "You are on a WhatsApp call. Calls rank above focus sessions and music.",
            },
            CallPhase.Ended => new Activity(Id, Priority.Status, DateTimeOffset.UtcNow)
            {
                Reason = "Your WhatsApp call just ended. This goes away in a few seconds.",
            },
            CallPhase.Missed => new Activity(Id, Priority.Status, _missedAt)
            {
                Reason = $"You missed a WhatsApp call from {Caller}. It stays until you call back or dismiss it.",
            },
            _ => null,
        });
    }

    private void Hand(CallAction action)
    {
        _handledByUser = true;
        _ = HandAsync(action);
    }

    private async Task HandAsync(CallAction action)
    {
        if (!await _perform(action))
        {
            _showApp(); // couldn't press it for you: bring WhatsApp up so you can
        }
    }

    private void SetCaller(string caller)
    {
        if (caller == Caller)
        {
            return;
        }

        Caller = caller;
        var (from, to) = AvatarColors[(int)((uint)StableHash(caller) % AvatarColors.Length)];
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        AvatarBrush = brush;
    }

    /// <summary>The same colour for the same person every time (string.GetHashCode changes per run).</summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text)
            {
                hash = hash * 31 + c;
            }

            return hash;
        }
    }

    private void Refresh() => OnAllPropertiesChanged();

    private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;
}
