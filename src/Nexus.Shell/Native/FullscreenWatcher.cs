using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Nexus.Shell.Native;

/// <summary>The app window the user is working in: where it is and whose it is.</summary>
internal sealed record ForegroundInfo(IntPtr Hwnd, Win32.RECT Bounds, string Process, string Title);

/// <summary>
/// Watches the window in front, for two things:
/// <list type="bullet">
/// <item>Fullscreen apps, games and presentations, so Nexus hides.</item>
/// <item>Which app is in front and where its window is, so the island can keep clear of its
/// toolbars and status bars (smart positioning).</item>
/// </list>
/// Checks when the foreground window changes, when a window finishes moving, resizing, minimizing or
/// restoring, plus a light 1 s check for changes that raise no event (the maximize button).
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
    ];

    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private readonly Func<IntPtr> _ownWindow;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly Win32.WinEventProc _foregroundCallback; // kept alive for the native hooks
    private readonly Win32.WinEventProc _layoutCallback;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _moveSizeHook;
    private readonly IntPtr _minimizeHook;
    private readonly DispatcherTimer _timer;

    /// <param name="ownWindow">The island window, which never counts as "in front".</param>
    public FullscreenWatcher(Func<IntPtr> ownWindow)
    {
        _ownWindow = ownWindow;

        _foregroundCallback = (_, _, _, _, _, _, _) =>
        {
            Check();
            ForegroundChanged?.Invoke();
        };
        _layoutCallback = (_, _, _, _, _, _, _) => Check();

        _foregroundHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundCallback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _moveSizeHook = Win32.SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _layoutCallback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _minimizeHook = Win32.SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _layoutCallback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public bool IsFullscreen { get; private set; }

    /// <summary>The app window in front, or null on the desktop (or behind a fullscreen app).</summary>
    public ForegroundInfo? Foreground { get; private set; }

    public event Action<bool>? Changed;

    /// <summary>Another app came to the front, or its window moved, resized or changed title.</summary>
    public event Action<ForegroundInfo?>? WorkspaceChanged;

    /// <summary>Raised whenever another window comes to the front, so the island can re-assert topmost.</summary>
    public event Action? ForegroundChanged;

    public void Check()
    {
        var fg = Win32.GetForegroundWindow();

        // Our own windows (the island, its menus, the setup screen) and the shell (Start, taskbar,
        // Alt+Tab) say nothing about what the user is working in, so keep the current state.
        if (fg == IntPtr.Zero || fg == _ownWindow() || IsOwnProcess(fg) || ShellClasses.Contains(Win32.ClassName(fg)))
        {
            if (fg != IntPtr.Zero && Win32.ClassName(fg) is "Progman" or "WorkerW")
            {
                // Desktop in front: nothing covers the island.
                Update(fullscreen: false, foreground: null);
            }

            return;
        }

        var fullscreen = DetectFullscreen(fg);
        Update(fullscreen, fullscreen ? null : Describe(fg));
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var hook in new[] { _foregroundHook, _moveSizeHook, _minimizeHook })
        {
            if (hook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(hook);
            }
        }
    }

    private void Update(bool fullscreen, ForegroundInfo? foreground)
    {
        if (fullscreen != IsFullscreen)
        {
            DebugLog.Write($"Fullscreen {fullscreen}");
            IsFullscreen = fullscreen;
            Changed?.Invoke(fullscreen);
        }

        if (foreground != Foreground)
        {
            Foreground = foreground;
            WorkspaceChanged?.Invoke(foreground);
        }
    }

    private IntPtr _describedWindow;
    private string _describedProcess = "";

    private ForegroundInfo? Describe(IntPtr fg)
    {
        if (Win32.IsIconic(fg) || !Win32.IsWindowVisible(fg) || IsCloaked(fg) || !VisibleBounds(fg, out var bounds))
        {
            return null;
        }

        // The process name only changes with the window, so look it up once per window.
        if (fg != _describedWindow)
        {
            _describedWindow = fg;
            Win32.GetWindowThreadProcessId(fg, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                _describedProcess = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _describedProcess = "";
            }
        }

        return new ForegroundInfo(fg, bounds, _describedProcess, Win32.WindowText(fg));
    }

    private bool DetectFullscreen(IntPtr fg)
    {
        if (Win32.SHQueryUserNotificationState(out var state) == 0 &&
            state is Win32.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        // Borderless fullscreen (browser F11, video players, many games) covers its whole monitor.
        if (!VisibleBounds(fg, out var rect))
        {
            return false;
        }

        var monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    /// <summary>The window's visible frame, without the invisible resize borders Windows adds around it.</summary>
    private static bool VisibleBounds(IntPtr hwnd, out Win32.RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<Win32.RECT>()) == 0)
        {
            return true;
        }

        return Win32.GetWindowRect(hwnd, out rect);
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private bool IsOwnProcess(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Win32.RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
