using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace Nexus.Shell.Native;

/// <summary>
/// Smart collision detection: are there controls of the app in front where the island wants to sit?
/// It asks UI Automation, Windows' accessibility API, for the buttons, toolbars, text boxes, tabs,
/// menus and scroll bars inside an area of the app's window.
/// <para>
/// It walks down the app's own element tree rather than hit-testing the screen, so the island (which
/// is on top of that very area) never gets in its own way. Scrollable content (text, lists, links,
/// images) does not count: only things a user needs to reach at a fixed place.
/// </para>
/// Not every app describes itself well, and apps built on Chromium (Figma, VS Code, browsers) only
/// start answering a moment after first being asked, so callers ask again shortly afterwards and keep
/// per-app rules for the apps that matter. Takes up to a few hundred milliseconds: call off the UI thread.
/// </summary>
internal static class ControlProbe
{
    private static readonly HashSet<int> Controls =
    [
        ControlType.Button.Id, ControlType.SplitButton.Id, ControlType.CheckBox.Id, ControlType.RadioButton.Id,
        ControlType.ComboBox.Id, ControlType.Edit.Id, ControlType.Slider.Id, ControlType.Spinner.Id,
        ControlType.Tab.Id, ControlType.TabItem.Id, ControlType.MenuBar.Id, ControlType.Menu.Id,
        ControlType.MenuItem.Id, ControlType.ToolBar.Id, ControlType.ScrollBar.Id, ControlType.Thumb.Id,
    ];

    private const int MaxDepth = 48;
    private const int MaxVisited = 600;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// True when a control of <paramref name="window"/> overlaps <paramref name="area"/> (physical pixels).
    /// False when the area is clear, and also when the app can't be read: an island that stays put is
    /// better than one that moves on a guess.
    /// </summary>
    public static bool HasControls(IntPtr window, Win32.RECT area, double scale, out string? found)
    {
        found = null;
        var watch = Stopwatch.StartNew();
        try
        {
            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.NameProperty);

            var zone = new Rect(area.Left, area.Top, area.Width, area.Height);
            // A text box or tab strip is a control; an editor that fills the window is content.
            var maxControlHeight = Math.Max(140 * scale, zone.Height * 3);
            var visited = 0;

            using (cache.Activate())
            {
                var root = AutomationElement.FromHandle(window);
                found = Walk(root, 0);
            }

            return found is not null;

            string? Walk(AutomationElement element, int depth)
            {
                if (depth > MaxDepth || visited > MaxVisited || watch.Elapsed > Budget)
                {
                    return null;
                }

                AutomationElementCollection children;
                try
                {
                    children = element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException)
                {
                    return null;
                }

                // Later children are drawn on top, so look at them first.
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    var child = children[i];
                    visited++;

                    Rect bounds;
                    ControlType type;
                    try
                    {
                        var info = child.Cached;
                        bounds = info.BoundingRectangle;
                        type = info.ControlType;
                        if (info.IsOffscreen)
                        {
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                    {
                        continue;
                    }

                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || !Overlaps(bounds, zone))
                    {
                        continue;
                    }

                    if (Controls.Contains(type.Id) && (bounds.Height <= maxControlHeight || type == ControlType.ScrollBar))
                    {
                        return Describe(child, type, bounds);
                    }

                    if (Walk(child, depth + 1) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException or ArgumentException)
        {
            DebugLog.Write($"Control probe failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            DebugLog.Write($"Control probe {area.Left},{area.Top} {area.Width}x{area.Height}: {found ?? "clear"} in {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>Overlap that matters: more than a sliver of the control is inside the zone.</summary>
    private static bool Overlaps(Rect bounds, Rect zone)
    {
        var overlap = Rect.Intersect(bounds, zone);
        return !overlap.IsEmpty && overlap.Width >= 3 && overlap.Height >= 3;
    }

    private static string Describe(AutomationElement element, ControlType type, Rect bounds)
    {
        string name;
        try
        {
            name = element.Cached.Name ?? "";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            name = "";
        }

        if (name.Length > 24)
        {
            name = name[..24];
        }

        return $"{type.ProgrammaticName.Replace("ControlType.", "")} '{name}' {bounds.Width:N0}x{bounds.Height:N0}";
    }
}
