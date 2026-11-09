using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Focus;
using Nexus.Core.Settings;

namespace Nexus.Modes.Focus;

/// <summary>
/// F1. Focus timer with Focus Guard. Compact: progress ring and minutes left (plus guarded dots).
/// Expanded: time left, session count today, pause and stop. At the end: a soft chime and the
/// "Focus session done" banner, then the break.
/// </summary>
public sealed class FocusMode : IslandMode
{
    private static readonly TimeSpan DoneExpiresAfter = TimeSpan.FromMinutes(5);

    private readonly FocusTimer _timer = new();
    private readonly NexusSettings _settings;
    private readonly Action _saveSettings;
    private readonly DispatcherTimer _tick;
    private DateTimeOffset _doneAt;
    private IReadOnlyList<string> _guardedNames = [];

    public FocusMode(NexusSettings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _timer.AutoStartBreak = settings.Focus.AutoStartBreak;
        _timer.Changed += OnTimerChanged;
        _timer.PhaseCompleted += OnPhaseCompleted;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();

        PauseResumeCommand = new RelayCommand(() => _timer.TogglePause());
        StopCommand = new RelayCommand(() => _timer.Stop());
        StartBreakCommand = new RelayCommand(() => _timer.StartBreak());
        StartDefaultCommand = new RelayCommand(() => Start(DefaultPreset));
    }

    public override string Id => "focus";
    public override string Name => "Focus";

    public ICommand PauseResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand StartBreakCommand { get; }
    public ICommand StartDefaultCommand { get; }

    public FocusPhase Phase => _timer.Phase;
    public bool IsFocus => _timer.Phase == FocusPhase.Focus;
    public bool IsBreak => _timer.Phase == FocusPhase.Break;
    public bool IsDone => _timer.Phase == FocusPhase.Done;
    public bool IsRunning => _timer.IsRunning;
    public bool IsPaused => _timer.IsPaused;

    public FocusPreset DefaultPreset => PresetByName(_settings.Focus.DefaultPreset);

    public FocusPreset CustomPreset =>
        new("Custom", Math.Clamp(_settings.Focus.CustomFocusMinutes, 1, 240), Math.Clamp(_settings.Focus.CustomBreakMinutes, 1, 120));

    public string TimeText => Format(_timer.Remaining);
    public double Progress => _timer.Progress;

    /// <summary>Caption next to the time in the compact pill.</summary>
    public string CompactCaption => _timer.IsPaused ? "paused" : "left";

    public string BreakCaption => _timer.IsPaused ? "paused" : $"{TimeText} left";

    public string HeaderTitle => IsBreak ? "Break" : "Focus";

    public string PanelCaption => _timer.Phase switch
    {
        FocusPhase.Break => _timer.IsPaused
            ? $"paused · {_timer.Preset.BreakMinutes} min break"
            : $"left of {_timer.Preset.BreakMinutes} min break",
        _ => _timer.IsPaused
            ? $"paused · {_timer.Preset.FocusMinutes} min session"
            : $"left of {_timer.Preset.FocusMinutes} min",
    };

    public string SessionText
    {
        get
        {
            var done = SessionsToday;
            var current = IsFocus ? done + 1 : done;
            var goal = Math.Max(_settings.Focus.DailyGoal, current);
            return IsBreak ? $"{done} done today" : $"Session {current} of {goal}";
        }
    }

    public string PauseText => _timer.IsPaused ? "Resume" : "Pause";
    public string StopText => IsBreak ? "End break" : "Stop";

    public string DoneSubtitle =>
        $"{_timer.Preset.FocusMinutes} min  ·  {SessionsToday} session{(SessionsToday == 1 ? "" : "s")} today";

    /// <summary>One dot per mode waiting behind Focus Guard.</summary>
    public IReadOnlyList<int> GuardedDots { get; private set; } = [];

    public string GuardText => _guardedNames.Count == 0
        ? "Focus Guard on: other modes wait as dots"
        : $"Focus Guard on: {JoinNames(_guardedNames)} wait{(_guardedNames.Count == 1 ? "s" : "")} as dots";

    private int SessionsToday
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return _settings.Focus.SessionsDate == today ? _settings.Focus.SessionsToday : 0;
        }
    }

    public void Start(FocusPreset preset) => _timer.Start(preset);

    public void Stop() => _timer.Stop();

    public void ToggleDefault()
    {
        if (_timer.Phase == FocusPhase.Idle)
        {
            Start(DefaultPreset);
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>The shell tells Focus which modes are waiting behind the guard.</summary>
    public void SetGuarded(IReadOnlyList<string> names)
    {
        if (names.SequenceEqual(_guardedNames))
        {
            return;
        }

        _guardedNames = names;
        GuardedDots = Enumerable.Range(0, Math.Min(names.Count, 3)).ToArray();
        OnPropertyChanged(nameof(GuardedDots));
        OnPropertyChanged(nameof(GuardText));
    }

    public override void OnMomentEnded(MomentKind kind)
    {
        if (kind == MomentKind.Banner && IsDone)
        {
            // Banner settles into the compact "Session done" pill until the user acts or it expires.
            Refresh();
        }
    }

    protected override FrameworkElement CreateCompactView() => new FocusCompactView();
    protected override FrameworkElement CreatePanelView() => new FocusPanelView();
    protected override FrameworkElement CreateBannerView() => new FocusBannerView();
    protected override FrameworkElement? CreateBubbleView() => new FocusBubbleView();

    private void OnTimerChanged()
    {
        if (_timer.Phase == FocusPhase.Done)
        {
            _doneAt = DateTimeOffset.UtcNow;
        }

        // Tick while a phase runs, and once a second while "done" so it can expire.
        _tick.IsEnabled = _timer.Phase != FocusPhase.Idle;
        Refresh();
    }

    private void OnTick()
    {
        _timer.Tick();
        if (_timer.Phase == FocusPhase.Done && DateTimeOffset.UtcNow - _doneAt > DoneExpiresAfter)
        {
            _timer.Stop();
            return;
        }

        if (_timer.IsRunning && !_timer.IsPaused)
        {
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(BreakCaption));
        }
    }

    private void OnPhaseCompleted(FocusPhase ended)
    {
        if (ended == FocusPhase.Focus)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_settings.Focus.SessionsDate != today)
            {
                _settings.Focus.SessionsDate = today;
                _settings.Focus.SessionsToday = 0;
            }

            _settings.Focus.SessionsToday++;
            _saveSettings();
            Chime();
            Refresh();
            RequestMoment(_timer.Phase == FocusPhase.Done ? MomentKind.Banner : MomentKind.Panel);
        }
        else if (ended == FocusPhase.Break)
        {
            Chime();
        }
    }

    private void Refresh()
    {
        OnAllPropertiesChanged();

        var now = DateTimeOffset.UtcNow;
        Activity? activity = _timer.Phase switch
        {
            FocusPhase.Focus => new Activity(Id, Priority.FocusSession, Activity?.Since ?? now)
            {
                GuardsFocus = true,
                Reason = "A focus session is running. Focus holds the island and lower modes wait as dots until it ends.",
            },
            FocusPhase.Break => new Activity(Id, Priority.FocusSession, now)
            {
                Reason = "Your break timer is running.",
            },
            FocusPhase.Done => new Activity(Id, Priority.Status, now)
            {
                Reason = "Your focus session just finished.",
            },
            _ => null,
        };

        // Keep the same Since while the phase is unchanged so ordering stays stable.
        if (activity is not null && Activity is not null && Activity.Priority == activity.Priority && Activity.GuardsFocus == activity.GuardsFocus)
        {
            activity = activity with { Since = Activity.Since };
        }

        SetActivity(activity);
    }

    private void Chime()
    {
        if (!_settings.Focus.Chime)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
            if (File.Exists(path))
            {
                using var player = new SoundPlayer(path);
                player.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Sound is a nicety; never fail the timer because of it.
        }
    }

    private FocusPreset PresetByName(string name) => name switch
    {
        "50/10" => FocusPreset.Long,
        "Custom" => CustomPreset,
        _ => FocusPreset.Short,
    };

    private static string Format(TimeSpan t)
    {
        var seconds = (int)Math.Ceiling(t.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        var lower = names.Select(n => n.ToLowerInvariant()).ToList();
        return lower.Count switch
        {
            1 => lower[0],
            2 => $"{lower[0]} and {lower[1]}",
            _ => string.Join(", ", lower.Take(lower.Count - 1)) + " and " + lower[^1],
        };
    }
}
