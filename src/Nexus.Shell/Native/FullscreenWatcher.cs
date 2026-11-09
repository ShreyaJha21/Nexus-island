using System.Windows.Threading;

namespace Nexus.Shell.Native;

/// <summary>
/// Detects fullscreen apps, games and presentations so Nexus can get out of the way.
/// Checks when the foreground window changes, plus a slow 2 s check for apps that go
/// fullscreen without changing focus (F11 in a browser, a video player).
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly Func<IntPtr> _ownWindow;
    private readonly Win32.WinEventProc _callback; // keep alive for the native hook
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _timer;

    public FullscreenWatcher(Func<IntPtr> ownWindow)
    {
        _ownWindow = ownWindow;
        _callback = (_, _, _, _, _, _, _) =>
        {
            Check();
            ForegroundChanged?.Invoke();
        };
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? Changed;

    /// <summary>Raised whenever another window comes to the front, so the island can re-assert topmost.</summary>
    public event Action? ForegroundChanged;

    public void Check()
    {
        var fullscreen = Detect();
        if (fullscreen != IsFullscreen)
        {
            DebugLog.Write($"Fullscreen {fullscreen}");
            IsFullscreen = fullscreen;
            Changed?.Invoke(fullscreen);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_hook);
        }
    }

    private bool Detect()
    {
        if (Win32.SHQueryUserNotificationState(out var state) == 0 &&
            state is Win32.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        // Borderless fullscreen (browser F11, video players, many games) covers its whole monitor.
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _ownWindow() || ShellClasses.Contains(Win32.ClassName(fg)))
        {
            return false;
        }

        if (!Win32.GetWindowRect(fg, out var rect))
        {
            return false;
        }

        var monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }
}
