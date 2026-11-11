using System.Text.RegularExpressions;

namespace Nexus.Core.Calls;

public enum CallState
{
    /// <summary>Someone is calling you.</summary>
    Incoming,
    /// <summary>You are calling someone: "Calling…", "Ringing…".</summary>
    Outgoing,
    /// <summary>On the call.</summary>
    Ongoing,
}

/// <summary>An action Nexus can hand to the calling app by pressing its own button.</summary>
public enum CallAction { Accept, Decline, End, Mute, Unmute }

/// <summary>One button or piece of text read from the calling app's window.</summary>
public readonly record struct CallUiItem(bool IsButton, string Name);

/// <summary>What the calling app shows right now.</summary>
public sealed record CallSnapshot(CallState State, string? Caller, bool IsVideo, bool IsMuted, TimeSpan? Duration)
{
    /// <summary>Which of Nexus's buttons can be handed to the app.</summary>
    public bool CanMute { get; init; }
    public bool CanEnd { get; init; }
    public bool CanAnswer { get; init; }

    /// <summary>The app is calling a group.</summary>
    public bool IsGroup { get; init; }
}

/// <summary>
/// Works out a call's state from the buttons and text the calling app exposes to Windows'
/// accessibility API. WhatsApp names its call buttons "Accept", "Decline", "End call",
/// "Mute microphone" and "Unmute microphone", and labels states "Incoming voice call.", "Ringing…",
/// "Calling…". Pure, so it is easy to test against what the app really shows.
/// </summary>
public static class CallReader
{
    public static readonly string[] AcceptNames = ["Accept", "Accept call", "Answer", "Answer call"];
    public static readonly string[] DeclineNames = ["Decline", "Decline call", "Reject", "Reject call"];
    public static readonly string[] EndNames = ["End call", "Hang up", "Leave call", "End"];
    public static readonly string[] MuteNames = ["Mute microphone", "Mute", "Mute mic"];
    public static readonly string[] UnmuteNames = ["Unmute microphone", "Unmute", "Unmute mic"];

    /// <summary>Every button name Nexus looks for, to find a call quickly.</summary>
    public static IEnumerable<string> AllButtonNames =>
        AcceptNames.Concat(DeclineNames).Concat(EndNames).Concat(MuteNames).Concat(UnmuteNames);

    public static IReadOnlyList<string> NamesFor(CallAction action) => action switch
    {
        CallAction.Accept => AcceptNames,
        CallAction.Decline => DeclineNames,
        CallAction.End => EndNames,
        CallAction.Mute => MuteNames,
        _ => UnmuteNames,
    };

    private static readonly Regex Clock = new(@"^\s*(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\s*$", RegexOptions.Compiled);
    private static readonly Regex Outgoing = new(@"^(calling|ringing)\s*(\.\.\.|…)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Labels that describe the call rather than name the person on it.</summary>
    private static readonly Regex NotAName = new(
        @"^(whatsapp( voice| video)?( call)?|incoming|calling|ringing|reconnecting|connecting|on hold|muted|unmuted|" +
        @"end-to-end encrypted|voice call|video call|group (voice|video) call|call ended|you|me|.*\bcall\b.*|\d.*)" +
        @"\s*(\.\.\.|…|\.)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The call shown by these items, or null when they show no call. <paramref name="title"/> is the
    /// window's title (the call window is often titled with the caller's name). <paramref name="micInUse"/>
    /// says the app has the microphone open, which only happens during a call.
    /// </summary>
    public static CallSnapshot? Read(IReadOnlyCollection<CallUiItem> items, string? title, bool micInUse)
    {
        bool Has(string[] names) => items.Any(i => i.IsButton && names.Any(n => string.Equals(i.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)));

        var texts = items.Where(i => !i.IsButton).Select(i => i.Name.Trim()).Where(t => t.Length > 0).ToList();
        var all = texts.Append(title ?? "").ToList();

        var canAccept = Has(AcceptNames);
        var canDecline = Has(DeclineNames);
        var canEnd = Has(EndNames);
        var mute = Has(MuteNames);
        var unmute = Has(UnmuteNames);
        var outgoing = texts.Any(t => Outgoing.IsMatch(t));
        var incomingText = all.Any(t => t.StartsWith("Incoming", StringComparison.OrdinalIgnoreCase));

        CallState state;
        if (canAccept || (canDecline && !canEnd) || (incomingText && !canEnd && !micInUse))
        {
            state = CallState.Incoming;
        }
        else if (canEnd && outgoing)
        {
            state = CallState.Outgoing;
        }
        else if (canEnd || mute || unmute || micInUse)
        {
            state = CallState.Ongoing;
        }
        else
        {
            return null;
        }

        TimeSpan? duration = null;
        foreach (var t in texts)
        {
            if (Clock.Match(t) is { Success: true } m)
            {
                var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                duration = new TimeSpan(hours, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                break;
            }
        }

        return new CallSnapshot(
            state,
            CallerName(texts, title),
            IsVideo: all.Any(t => t.Contains("video", StringComparison.OrdinalIgnoreCase)),
            IsMuted: unmute && !mute,
            Duration: state == CallState.Ongoing ? duration : null)
        {
            CanMute = mute || unmute,
            CanEnd = canEnd || (state == CallState.Incoming && canDecline),
            CanAnswer = canAccept,
            IsGroup = all.Any(t => t.Contains("group", StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>The caller: the window title when it names someone, otherwise the first text that does.</summary>
    public static string? CallerName(IEnumerable<string> texts, string? title)
    {
        if (IsName(title))
        {
            return title!.Trim();
        }

        return texts.FirstOrDefault(IsName)?.Trim();
    }

    private static bool IsName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        return t.Length is >= 1 and <= 60 && !NotAName.IsMatch(t) && !Clock.IsMatch(t);
    }

    /// <summary>"Priya Sharma" becomes "PS"; "priya" becomes "P"; a number becomes "#".</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => char.IsLetter(w[0]))
            .ToList();
        if (words.Count == 0)
        {
            return "#";
        }

        var first = char.ToUpperInvariant(words[0][0]);
        return words.Count == 1 ? first.ToString() : $"{first}{char.ToUpperInvariant(words[^1][0])}";
    }

    /// <summary>"0:07", "12:48", "1:02:05": how the island shows how long a call has run.</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
