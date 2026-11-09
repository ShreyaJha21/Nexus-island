namespace Nexus.Core;

/// <summary>
/// The priority ladder from the design's edge-case rules. Lower numbers win the island.
/// </summary>
public enum Priority
{
    /// <summary>Swatch, while the user is picking a color.</summary>
    SwatchPicking = 1,
    /// <summary>A coding agent is waiting for permission or input. Always expands, even in focus.</summary>
    AgentNeedsYou = 2,
    /// <summary>A call is ringing. Always expands, even in focus.</summary>
    CallRinging = 3,
    CallOngoing = 4,
    /// <summary>A focus session (or its break) holds the island; lower items shrink to dots.</summary>
    FocusSession = 5,
    MeetingSoon = 6,
    CricketMoment = 7,
    /// <summary>Agent working or done, call ended, focus complete.</summary>
    Status = 8,
    /// <summary>Cricket live score, next calendar event.</summary>
    Ambient = 9,
    Music = 10,
}

public static class PriorityExtensions
{
    /// <summary>Urgent items are never moved down by pinning, scrolling or pausing.</summary>
    public static bool IsUrgent(this Priority priority) =>
        priority is Priority.AgentNeedsYou or Priority.CallRinging;
}

/// <summary>
/// What a mode wants to show right now. Modes publish one of these (or nothing);
/// the <see cref="PriorityEngine"/> decides which one gets the island.
/// </summary>
/// <param name="ModeId">Stable id of the mode that owns this activity, e.g. "focus".</param>
/// <param name="Priority">Where the activity sits on the priority ladder.</param>
/// <param name="Since">When the activity last changed in a way that matters for ordering.</param>
public sealed record Activity(string ModeId, Priority Priority, DateTimeOffset Since)
{
    /// <summary>May take the island while a focus session is running.</summary>
    public bool BreaksFocus { get; init; }

    /// <summary>This activity is a running focus session, so lower items become dots.</summary>
    public bool GuardsFocus { get; init; }

    /// <summary>Plain-language answer to "Why am I seeing this?".</summary>
    public string Reason { get; init; } = "";
}
