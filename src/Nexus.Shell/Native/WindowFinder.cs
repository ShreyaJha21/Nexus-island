using System.Diagnostics;
using System.IO;

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

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "firefox", "opera", "opera_gx", "vivaldi", "arc", "zen", "floorp",
    };

    /// <summary>
    /// Which site a browser is playing music from, read from its window titles: YouTube titles its
    /// tabs "&lt;video&gt; - YouTube", YouTube Music "&lt;song&gt; - YouTube Music", and so on. A window whose
    /// title contains the track title wins; otherwise any browser window showing one of these sites.
    /// Returns "youtube", "youtubemusic", "spotify", "soundcloud" or null.
    /// </summary>
    public static string? MusicSite(string appId, string trackTitle)
    {
        string? bestMatch = null;
        string? anySite = null;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = Win32.WindowText(hwnd);
            var site = SiteFromTitle(title);
            if (site is null)
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!BrowserProcesses.Contains(process.ProcessName))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            anySite ??= site;
            if (!string.IsNullOrWhiteSpace(trackTitle) && title.Contains(trackTitle, StringComparison.OrdinalIgnoreCase))
            {
                bestMatch = site;
                return false; // found the tab that is playing
            }

            return true;
        }, IntPtr.Zero);

        return bestMatch ?? anySite;
    }

    private static string? SiteFromTitle(string title)
    {
        if (title.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase))
        {
            return "youtubemusic";
        }

        if (title.Contains("- YouTube", StringComparison.OrdinalIgnoreCase) || title.EndsWith("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return "youtube";
        }

        if (title.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "spotify";
        }

        if (title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
        {
            return "soundcloud";
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
