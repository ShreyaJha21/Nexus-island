using Nexus.Core.Settings;

namespace Nexus.Core.Positioning;

/// <summary>
/// The decisions behind smart positioning, kept free of Windows calls so they are easy to test:
/// which spot to use, which app rule applies, and whether it is a "stay compact" time of day.
/// </summary>
public static class SmartPlacement
{
    private static readonly string[] TitleSeparators = [" - ", " – ", " — ", " | "];

    /// <summary>Process names of web browsers.</summary>
    public static readonly IReadOnlySet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "chrome", "chromium", "msedge", "firefox", "opera", "opera_gx", "vivaldi", "arc", "zen",
        "floorp", "librewolf", "waterfox", "thorium", "yandex", "whale", "iexplore",
    };

    public static bool IsBrowser(string process) => Browsers.Contains(process);

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm"];

    /// <summary>
    /// The preferred spot when it is clear; otherwise the first clear candidate, in order; otherwise
    /// null (nowhere is clear). <paramref name="isBlocked"/> may be slow, so it is asked as little as possible.
    /// </summary>
    public static Placement? Choose(Placement preferred, IEnumerable<Placement> candidates, Func<Placement, bool> isBlocked)
    {
        if (!isBlocked(preferred))
        {
            return preferred;
        }

        var tried = new HashSet<Placement> { preferred };
        foreach (var spot in candidates)
        {
            // Custom only makes sense as the user's own preferred position.
            if (spot == Placement.Custom || !tried.Add(spot))
            {
                continue;
            }

            if (!isBlocked(spot))
            {
                return spot;
            }
        }

        return null;
    }

    /// <summary>
    /// The rule for the app in front. A key matches the process name ("Figma" for Figma.exe), or the
    /// app name a browser puts at the end of a tab's title ("Untitled – Figma - Google Chrome").
    /// </summary>
    public static AppRule? FindRule(IReadOnlyDictionary<string, AppRule>? rules, string process, string title) =>
        FindRuleKey(rules, process, title) is { } key ? rules![key] : null;

    public static string? FindRuleKey(IReadOnlyDictionary<string, AppRule>? rules, string process, string title)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        foreach (var key in rules.Keys)
        {
            if (key.Length > 0 && string.Equals(key, process, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        foreach (var key in rules.Keys)
        {
            if (key.Length > 0 && TitleNames(title, key))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>True when the title has the app name as its own dash-separated part, after the first: "File – Figma".</summary>
    private static bool TitleNames(string title, string app)
    {
        var parts = title.Split(TitleSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], app, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="now"/> falls inside one of the "HH:mm-HH:mm" ranges. Bad entries are ignored.</summary>
    public static bool IsCompactTime(IEnumerable<string>? ranges, TimeOnly now)
    {
        if (ranges is null)
        {
            return false;
        }

        foreach (var range in ranges)
        {
            var parts = range.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TimeOnly.TryParseExact(parts[0], TimeFormats, out var from) ||
                !TimeOnly.TryParseExact(parts[1], TimeFormats, out var to))
            {
                continue;
            }

            var inside = from <= to ? now >= from && now < to : now >= from || now < to;
            if (inside)
            {
                return true;
            }
        }

        return false;
    }
}
