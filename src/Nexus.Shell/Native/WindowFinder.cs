using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Nexus.Core.Positioning;

namespace Nexus.Shell.Native;

/// <summary>
/// Brings the agent's terminal or editor forward for "Open terminal". Nexus cannot answer
/// a permission prompt itself; it only hands off to the window where the user can.
/// </summary>
internal static class WindowFinder
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh", "conhost", "claude",
        "Code", "Code - Insiders", "Cursor", "Windsurf", "idea64", "rider64", "pycharm64", "webstorm64",
        "wezterm-gui", "alacritty", "mintty", "Hyper", "Tabby", "warp", "ghostty", "kitty", "zed",
    };

    /// <summary>Focuses the best matching window. Returns false if none was found.</summary>
    public static bool FocusTerminal(string? cwd)
    {
        var project = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('/', '\\'));
        IntPtr best = IntPtr.Zero;
        var bestScore = 0;

        // EnumWindows walks top-level windows in z-order, so the first match of a score is the most recent.
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            var title = Win32.WindowText(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            string name;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (ArgumentException)
            {
                return true;
            }

            if (!TerminalProcesses.Contains(name) && Win32.ClassName(hwnd) != "ConsoleWindowClass")
            {
                return true;
            }

            var score = 1;
            if (project is not null && title.Contains(project, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (title.Contains("claude", StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero)
        {
            return false;
        }

        Focus(best);
        return true;
    }

    /// <summary>
    /// Which site a browser is playing music from. Windows' media session only names the browser, so
    /// this reads titles: YouTube titles its tabs "&lt;video&gt; - YouTube", YouTube Music
    /// "&lt;song&gt; - YouTube Music", SoundCloud "... | SoundCloud", and Spotify's web player
    /// "&lt;song&gt; • &lt;artist&gt;". In order:
    /// <list type="number">
    /// <item>a browser window whose title names the track (the playing tab is the active one);</item>
    /// <item>a tab, in any browser window, whose title names the track: music often plays in a
    /// background tab while you browse another, and the tab strip still lists it (UI Automation);</item>
    /// <item>any browser window showing one of these sites.</item>
    /// </list>
    /// Returns "youtube", "youtubemusic", "spotify", "soundcloud" or null. Reads other apps' UI, which
    /// can take a few hundred milliseconds: call it off the UI thread.
    /// </summary>
    public static string? MusicSite(string appId, string trackTitle, string artist)
    {
        var windows = BrowserWindows();
        string? anySite = null;

        foreach (var (_, title) in windows)
        {
            var site = SiteFromTitle(title, trackTitle, artist);
            if (site is null)
            {
                continue;
            }

            anySite ??= site;
            if (NamesTrack(title, trackTitle))
            {
                return site;
            }
        }

        if (!string.IsNullOrWhiteSpace(trackTitle))
        {
            foreach (var (hwnd, _) in windows)
            {
                foreach (var tab in TabTitles(hwnd))
                {
                    if (NamesTrack(tab, trackTitle) && SiteFromTitle(tab, trackTitle, artist) is { } site)
                    {
                        return site;
                    }
                }
            }
        }

        return anySite;
    }

    /// <summary>Visible top-level browser windows, front to back, with their titles.</summary>
    private static List<(IntPtr Hwnd, string Title)> BrowserWindows()
    {
        var found = new List<(IntPtr, string)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            var title = Win32.WindowText(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (SmartPlacement.IsBrowser(process.ProcessName))
                {
                    found.Add((hwnd, title));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The titles of a browser window's tabs, as its tab strip reports them to UI Automation.</summary>
    private static IEnumerable<string> TabTitles(IntPtr window)
    {
        var titles = new List<string>();
        try
        {
            var tabs = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement tab in tabs)
            {
                if (tab.Current.Name is { Length: > 0 } name)
                {
                    titles.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException)
        {
        }

        return titles;
    }

    private static bool NamesTrack(string title, string track) =>
        !string.IsNullOrWhiteSpace(track) && title.Contains(track.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? SiteFromTitle(string title, string track, string artist)
    {
        if (title.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase))
        {
            return "youtubemusic";
        }

        if (title.Contains("- YouTube", StringComparison.OrdinalIgnoreCase) || title.EndsWith("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return "youtube";
        }

        if (title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return "soundcloud";
        }

        // Spotify's web player titles the tab "<song> • <artist>" while it plays.
        if (title.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(track) && title.Contains($"{track.Trim()} • ", StringComparison.OrdinalIgnoreCase) &&
             (string.IsNullOrWhiteSpace(artist) || title.Contains(artist.Trim(), StringComparison.OrdinalIgnoreCase))))
        {
            return "spotify";
        }

        return null;
    }

    public static void Focus(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        }

        if (!Win32.SetForegroundWindow(hwnd))
        {
            // Windows only lets the app that received the last input switch focus. A tap of Alt
            // counts as that input, which is the standard way to hand focus on after a click.
            Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Win32.SetForegroundWindow(hwnd);
        }
    }
}
