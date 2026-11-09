namespace Nexus.Core.Focus;

public enum FocusPhase
{
    /// <summary>No session.</summary>
    Idle,
    /// <summary>A focus block is running (or paused).</summary>
    Focus,
    /// <summary>The focus block ended and the user has not started the break yet.</summary>
    Done,
    /// <summary>A break is running (or paused).</summary>
    Break,
}

public sealed record FocusPreset(string Name, int FocusMinutes, int BreakMinutes)
{
    public static FocusPreset Short { get; } = new("25/5", 25, 5);
    public static FocusPreset Long { get; } = new("50/10", 50, 10);

    public TimeSpan FocusDuration => TimeSpan.FromMinutes(FocusMinutes);
    public TimeSpan BreakDuration => TimeSpan.FromMinutes(BreakMinutes);
}

/// <summary>
/// Pomodoro-style timer. Time left is always computed from the clock rather than
/// counted in ticks, so it does not drift, and a paused session keeps its remaining
/// time through sleep and resume.
/// </summary>
public sealed class FocusTimer
{
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _endsAt;
    private TimeSpan _pausedRemaining;

    public FocusTimer(Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when a focus block or break runs out. The argument is the phase that just ended.</summary>
    public event Action<FocusPhase>? PhaseCompleted;

    /// <summary>Raised on any state change (start, pause, resume, stop, phase change).</summary>
    public event Action? Changed;

    public FocusPhase Phase { get; private set; } = FocusPhase.Idle;
    public FocusPreset Preset { get; private set; } = FocusPreset.Short;
    public bool IsPaused { get; private set; }
    public bool AutoStartBreak { get; set; }

    /// <summary>Length of the current phase.</summary>
    public TimeSpan Duration => Phase switch
    {
        FocusPhase.Focus or FocusPhase.Done => Preset.FocusDuration,
        FocusPhase.Break => Preset.BreakDuration,
        _ => TimeSpan.Zero,
    };

    public TimeSpan Remaining
    {
        get
        {
            if (Phase is FocusPhase.Idle or FocusPhase.Done)
            {
                return TimeSpan.Zero;
            }

            if (IsPaused)
            {
                return _pausedRemaining;
            }

            var left = _endsAt - _now();
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>0 at the start of the phase, 1 at the end.</summary>
    public double Progress => Duration <= TimeSpan.Zero
        ? (Phase == FocusPhase.Done ? 1 : 0)
        : Math.Clamp(1 - Remaining.TotalSeconds / Duration.TotalSeconds, 0, 1);

    public bool IsRunning => Phase is FocusPhase.Focus or FocusPhase.Break;

    public void Start(FocusPreset preset)
    {
        Preset = preset;
        Begin(FocusPhase.Focus, preset.FocusDuration);
    }

    public void StartBreak()
    {
        if (Phase is FocusPhase.Idle)
        {
            return;
        }

        Begin(FocusPhase.Break, Preset.BreakDuration);
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused)
        {
            return;
        }

        _pausedRemaining = Remaining;
        IsPaused = true;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused)
        {
            return;
        }

        _endsAt = _now() + _pausedRemaining;
        IsPaused = false;
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (IsPaused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    /// <summary>Ends the current phase now, as if its time had run out.</summary>
    public void Skip()
    {
        if (!IsRunning)
        {
            return;
        }

        Complete();
    }

    public void Stop()
    {
        if (Phase == FocusPhase.Idle)
        {
            return;
        }

        Phase = FocusPhase.Idle;
        IsPaused = false;
        Changed?.Invoke();
    }

    /// <summary>Call periodically (about once a second while visible) to detect the end of a phase.</summary>
    public void Tick()
    {
        if (IsRunning && !IsPaused && _now() >= _endsAt)
        {
            Complete();
        }
    }

    private void Begin(FocusPhase phase, TimeSpan duration)
    {
        Phase = phase;
        IsPaused = false;
        _endsAt = _now() + duration;
        Changed?.Invoke();
    }

    private void Complete()
    {
        var ended = Phase;
        if (ended == FocusPhase.Focus)
        {
            if (AutoStartBreak)
            {
                Begin(FocusPhase.Break, Preset.BreakDuration);
            }
            else
            {
                Phase = FocusPhase.Done;
                IsPaused = false;
                Changed?.Invoke();
            }
        }
        else
        {
            Phase = FocusPhase.Idle;
            IsPaused = false;
            Changed?.Invoke();
        }

        PhaseCompleted?.Invoke(ended);
    }
}
