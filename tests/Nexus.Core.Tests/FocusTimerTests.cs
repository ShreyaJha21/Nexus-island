using Nexus.Core.Focus;

namespace Nexus.Core.Tests;

public class FocusTimerTests
{
    private DateTimeOffset _now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private FocusTimer NewTimer() => new(() => _now);

    [Fact]
    public void Counts_down_from_the_clock_without_drift()
    {
        var timer = NewTimer();
        timer.Start(FocusPreset.Short);

        _now = _now.AddMinutes(10).AddSeconds(30);
        Assert.Equal(TimeSpan.FromMinutes(14.5), timer.Remaining);
        Assert.Equal(0.42, timer.Progress, 2);
    }

    [Fact]
    public void Pause_keeps_remaining_time_through_sleep()
    {
        var timer = NewTimer();
        timer.Start(FocusPreset.Short);
        _now = _now.AddMinutes(5);
        timer.Pause();

        _now = _now.AddHours(8); // laptop asleep overnight
        timer.Tick();
        Assert.Equal(FocusPhase.Focus, timer.Phase);
        Assert.Equal(TimeSpan.FromMinutes(20), timer.Remaining);

        timer.Resume();
        _now = _now.AddMinutes(20);
        timer.Tick();
        Assert.Equal(FocusPhase.Done, timer.Phase);
    }

    [Fact]
    public void Focus_ends_in_done_then_break_then_idle()
    {
        var timer = NewTimer();
        var ended = new List<FocusPhase>();
        timer.PhaseCompleted += ended.Add;

        timer.Start(FocusPreset.Short);
        _now = _now.AddMinutes(25);
        timer.Tick();
        Assert.Equal(FocusPhase.Done, timer.Phase);

        timer.StartBreak();
        Assert.Equal(TimeSpan.FromMinutes(5), timer.Remaining);
        _now = _now.AddMinutes(5);
        timer.Tick();

        Assert.Equal(FocusPhase.Idle, timer.Phase);
        Assert.Equal([FocusPhase.Focus, FocusPhase.Break], ended);
    }

    [Fact]
    public void Auto_start_break_skips_the_done_state()
    {
        var timer = NewTimer();
        timer.AutoStartBreak = true;
        timer.Start(FocusPreset.Long);
        _now = _now.AddMinutes(50);
        timer.Tick();
        Assert.Equal(FocusPhase.Break, timer.Phase);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Remaining);
    }

    [Fact]
    public void Drift_stays_under_a_second_per_hour()
    {
        var timer = NewTimer();
        timer.Start(new FocusPreset("Hour", 60, 5));
        for (var i = 0; i < 3599; i++)
        {
            _now = _now.AddSeconds(1);
            timer.Tick();
        }

        Assert.Equal(TimeSpan.FromSeconds(1), timer.Remaining);
    }
}
