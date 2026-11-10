using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Nexus.Shell.Native;

/// <summary>A horizontal stretch of the taskbar with nothing in it (physical pixels).</summary>
internal readonly record struct TaskbarGap(int Left, int Right, int Top, int Bottom)
{
    public int Width => Right - Left;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;
}

/// <summary>
/// Finds empty space in the taskbar for the island to tuck into. It reads where the taskbar's own
/// buttons are (Start, search, widgets, pinned and running apps, the system tray and clock) through
/// UI Automation, the accessibility API, and returns the free gaps between them.
/// UI Automation can take a few hundred milliseconds, so call this off the UI thread.
/// </summary>
internal static class TaskbarLayout
{
    private static readonly ControlType[] Occupying =
    [
        ControlType.Button, ControlType.SplitButton, ControlType.ListItem, ControlType.MenuItem,
        ControlType.ToolBar, ControlType.Text, ControlType.Image, ControlType.Edit,
    ];

    /// <summary>All free gaps of at least <paramref name="minWidth"/> pixels, or null when the taskbar can't be read.</summary>
    public static IReadOnlyList<TaskbarGap>? FindGaps(int minWidth)
    {
        var bar = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        if (Win32.SHAppBarMessage(Win32.ABM_GETTASKBARPOS, ref bar) == UIntPtr.Zero || bar.uEdge != Win32.ABE_BOTTOM)
        {
            return null;
        }

        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            return null;
        }

        List<(int Left, int Right)> taken;
        try
        {
            taken = OccupiedSpans(tray, bar.rc);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }

        if (taken.Count == 0)
        {
            return null; // couldn't see any buttons; don't guess
        }

        return Gaps(taken, bar.rc, minWidth);
    }

    /// <summary>Pure gap arithmetic, separate so it is easy to reason about.</summary>
    internal static List<TaskbarGap> Gaps(List<(int Left, int Right)> taken, Win32.RECT bar, int minWidth)
    {
        const int padding = 12; // keep clear of the buttons on either side
        var merged = taken
            .Select(s => (Left: Math.Max(bar.Left, s.Left - padding), Right: Math.Min(bar.Right, s.Right + padding)))
            .OrderBy(s => s.Left)
            .Aggregate(new List<(int Left, int Right)>(), (list, s) =>
            {
                if (list.Count > 0 && s.Left <= list[^1].Right)
                {
                    list[^1] = (list[^1].Left, Math.Max(list[^1].Right, s.Right));
                }
                else
                {
                    list.Add(s);
                }

                return list;
            });

        var gaps = new List<TaskbarGap>();
        var cursor = bar.Left;
        foreach (var span in merged)
        {
            if (span.Left - cursor >= minWidth)
            {
                gaps.Add(new TaskbarGap(cursor, span.Left, bar.Top, bar.Bottom));
            }

            cursor = Math.Max(cursor, span.Right);
        }

        if (bar.Right - cursor >= minWidth)
        {
            gaps.Add(new TaskbarGap(cursor, bar.Right, bar.Top, bar.Bottom));
        }

        return gaps;
    }

    private static List<(int Left, int Right)> OccupiedSpans(IntPtr tray, Win32.RECT bar)
    {
        var root = AutomationElement.FromHandle(tray);
        var condition = new OrCondition(Occupying
            .Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t))
            .ToArray());

        var spans = new List<(int, int)>();
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
        {
            var r = element.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }

            // Ignore anything not on the taskbar itself, and containers as wide as the taskbar.
            if (r.Bottom <= bar.Top || r.Top >= bar.Bottom || r.Width > (bar.Right - bar.Left) * 0.8)
            {
                continue;
            }

            spans.Add(((int)r.Left, (int)r.Right));
        }

        return spans;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
