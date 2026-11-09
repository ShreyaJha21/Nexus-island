using Nexus.Core;

namespace Nexus.Core.Tests;

public class PriorityEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    private static Activity Music => new("music", Priority.Music, T0);
    private static Activity AgentWorking => new("agent", Priority.Status, T0);
    private static Activity AgentNeedsYou => new("agent", Priority.AgentNeedsYou, T0) { BreaksFocus = true };
    private static Activity Focus => new("focus", Priority.FocusSession, T0) { GuardsFocus = true };

    [Fact]
    public void Nothing_active_rests()
    {
        var layout = PriorityEngine.Arrange([]);
        Assert.Null(layout.Primary);
        Assert.Null(layout.Secondary);
    }

    [Fact]
    public void Higher_priority_keeps_the_island_and_the_next_becomes_a_bubble()
    {
        var layout = PriorityEngine.Arrange([Music, AgentWorking]);
        Assert.Equal("agent", layout.Primary!.ModeId);
        Assert.Equal("music", layout.Secondary!.ModeId);
        Assert.Equal(0, layout.OverflowCount);
    }

    [Fact]
    public void A_third_activity_becomes_a_plus_N_chip()
    {
        var layout = PriorityEngine.Arrange([Music, AgentWorking, new Activity("cricket", Priority.Ambient, T0)]);
        Assert.Equal(1, layout.OverflowCount);
    }

    [Fact]
    public void Focus_guard_turns_lower_items_into_dots()
    {
        var layout = PriorityEngine.Arrange([Music, AgentWorking, Focus]);
        Assert.Equal("focus", layout.Primary!.ModeId);
        Assert.Null(layout.Secondary);
        Assert.Equal(["agent", "music"], layout.Guarded.Select(a => a.ModeId).OrderBy(x => x));
    }

    [Fact]
    public void Agent_needs_you_breaks_through_focus()
    {
        var layout = PriorityEngine.Arrange([Music, AgentNeedsYou, Focus]);
        Assert.Equal("agent", layout.Primary!.ModeId);
        Assert.Equal("focus", layout.Secondary!.ModeId);
        Assert.Single(layout.Guarded);
    }

    [Fact]
    public void Scroll_selection_and_pin_bend_the_order()
    {
        Assert.Equal("music", PriorityEngine.Arrange([Music, AgentWorking], new ArrangeOptions(SelectedModeId: "music")).Primary!.ModeId);
        Assert.Equal("music", PriorityEngine.Arrange([Music, AgentWorking], new ArrangeOptions(PinnedModeId: "music")).Primary!.ModeId);
    }

    [Fact]
    public void Urgent_items_ignore_pin_and_pause()
    {
        var options = new ArrangeOptions(PinnedModeId: "music", PausedModeIds: new HashSet<string> { "agent" });
        Assert.Equal("agent", PriorityEngine.Arrange([Music, AgentNeedsYou], options).Primary!.ModeId);
    }

    [Fact]
    public void Paused_modes_are_hidden()
    {
        var layout = PriorityEngine.Arrange([Music, AgentWorking], new ArrangeOptions(PausedModeIds: new HashSet<string> { "agent" }));
        Assert.Equal("music", layout.Primary!.ModeId);
        Assert.Null(layout.Secondary);
    }

    [Fact]
    public void Scrolling_cycles_through_visible_modes()
    {
        var layout = PriorityEngine.Arrange([Music, AgentWorking]);
        Assert.Equal("music", PriorityEngine.NextModeId(layout, 1));
        Assert.Equal("music", PriorityEngine.NextModeId(layout, -1));
    }
}
