namespace Nexus.Core;

/// <summary>What the island shows after arranging all current activities.</summary>
/// <param name="Primary">Owns the island, or null when nothing is active (resting line).</param>
/// <param name="Secondary">Shown as the 32 px detached bubble.</param>
/// <param name="OverflowCount">Further items, shown as a +N chip.</param>
/// <param name="Guarded">Items held back by Focus Guard, shown as dots beside the focus ring.</param>
/// <param name="Visible">Everything that may own the island, in order. Scrolling cycles through this.</param>
public sealed record IslandLayout(
    Activity? Primary,
    Activity? Secondary,
    int OverflowCount,
    IReadOnlyList<Activity> Guarded,
    IReadOnlyList<Activity> Visible)
{
    public static IslandLayout Empty { get; } = new(null, null, 0, [], []);
}

/// <summary>User choices that bend the natural order (never for urgent items).</summary>
/// <param name="SelectedModeId">Chosen by scrolling on the island; transient.</param>
/// <param name="PinnedModeId">Chosen with "Pin this mode"; lasts until unpinned.</param>
/// <param name="PausedModeIds">Paused with "Pause for 1 hour".</param>
public sealed record ArrangeOptions(
    string? SelectedModeId = null,
    string? PinnedModeId = null,
    IReadOnlySet<string>? PausedModeIds = null);

/// <summary>
/// Picks one thing to show, following the fixed priority ladder in the PRD:
/// the island shows one mode at a time, the next becomes a bubble, the rest a +N chip,
/// and a running focus session turns everything below it into dots.
/// </summary>
public static class PriorityEngine
{
    public static IslandLayout Arrange(IEnumerable<Activity> activities, ArrangeOptions? options = null)
    {
        options ??= new ArrangeOptions();
        var paused = options.PausedModeIds ?? new HashSet<string>();

        var ordered = activities
            .Where(a => a.Priority.IsUrgent() || !paused.Contains(a.ModeId))
            .OrderBy(a => (int)a.Priority)
            .ThenByDescending(a => a.Since)
            .ToList();

        if (ordered.Count == 0)
        {
            return IslandLayout.Empty;
        }

        // Focus Guard: while a session runs, anything ranked below it waits as a dot
        // unless it is explicitly allowed to break focus.
        var guard = ordered.FirstOrDefault(a => a.GuardsFocus);
        var guarded = guard is null
            ? new List<Activity>()
            : ordered.Where(a => !ReferenceEquals(a, guard) && a.Priority > guard.Priority && !a.BreaksFocus).ToList();

        var visible = ordered.Where(a => !guarded.Contains(a)).ToList();

        var primary = visible[0];
        if (!primary.Priority.IsUrgent())
        {
            var preferred =
                Find(visible, options.SelectedModeId) ??
                Find(visible, options.PinnedModeId);

            if (preferred is not null)
            {
                primary = preferred;
            }
        }

        var rest = visible.Where(a => !ReferenceEquals(a, primary)).ToList();
        var secondary = rest.FirstOrDefault();
        var overflow = Math.Max(0, rest.Count - 1);

        return new IslandLayout(primary, secondary, overflow, guarded, visible);
    }

    /// <summary>The mode to select when the user scrolls on the island.</summary>
    public static string? NextModeId(IslandLayout layout, int direction)
    {
        if (layout.Visible.Count < 2 || layout.Primary is null)
        {
            return layout.Primary?.ModeId;
        }

        var index = layout.Visible.ToList().FindIndex(a => a.ModeId == layout.Primary.ModeId);
        var count = layout.Visible.Count;
        var next = ((index + Math.Sign(direction)) % count + count) % count;
        return layout.Visible[next].ModeId;
    }

    private static Activity? Find(List<Activity> visible, string? modeId) =>
        modeId is null ? null : visible.FirstOrDefault(a => a.ModeId == modeId);
}
