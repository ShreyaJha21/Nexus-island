using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;
using Nexus.Core.Calls;

namespace Nexus.Shell.Native;

/// <summary>
/// Mirrors WhatsApp calls. WhatsApp has no API for this, so Nexus reads what WhatsApp shows: its call
/// window, its in-app call bar and its call notifications, through UI Automation (the accessibility
/// API that screen readers use), and presses WhatsApp's own Accept, Decline, Mute and End call
/// buttons for you. Nothing is sent anywhere and Nexus never touches the call audio.
/// <para>
/// Reading another app's interface costs a little, so it only happens when there is a reason to:
/// WhatsApp is playing sound (a ringtone or call audio), has the microphone open, has a call window
/// or a notification up, or a call is already showing. Those checks are cheap and run once a second.
/// </para>
/// </summary>
internal sealed class CallWatcher : IDisposable
{
    private const string MicrophoneStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private static readonly TimeSpan MainScanInterval = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _apps = new(StringComparer.OrdinalIgnoreCase) { "WhatsApp.Root", "WhatsApp" };

    /// <summary>Testing with a stand-in app: WhatsApp, its notifications and its microphone use are ignored.</summary>
    private readonly bool _testMode;
    private readonly DispatcherTimer _timer;
    private HashSet<int> _pids = [];
    private DateTime _pidsCheckedAt;
    private DateTime _mainScannedAt;
    private bool _scanning;
    private IntPtr _callWindow;
    private string _lastSignals = "";
    private string _lastDump = "";
    private readonly double[] _costs = new double[4];
    private int _costTicks;

    /// <summary>
    /// Where the call's controls were last found. Re-reading just that part each second is quick,
    /// even when the call sits inside WhatsApp's big main window. Only touched by the scan.
    /// </summary>
    private (AutomationElement Element, string Title, bool IsToast)? _container;

    public CallWatcher()
    {
        // NEXUS_CALL_APPS=myapp watches only that process instead of WhatsApp: for testing with a
        // stand-in window, so a test can never press a button on a real call.
        var testApps = (Environment.GetEnvironmentVariable("NEXUS_CALL_APPS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (testApps.Length > 0)
        {
            _testMode = true;
            _apps.Clear();
            _apps.UnionWith(testApps);
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The call shown right now (null: none), and when WhatsApp opened the microphone for it.</summary>
    public event Action<CallSnapshot?, DateTimeOffset?>? Changed;

    public CallSnapshot? Current { get; private set; }

    public void Start()
    {
        _timer.Start();
        Tick();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>Asks for a fresh look soon, e.g. right after pressing one of WhatsApp's buttons.</summary>
    public void CheckSoon()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Start();
    }

    /// <summary>
    /// Presses WhatsApp's own button for this action. Returns false when it couldn't be found or
    /// pressed; the caller then brings WhatsApp to the front so the user can do it there.
    /// </summary>
    public async Task<bool> PerformAsync(CallAction action)
    {
        var targets = Targets(includeMain: true).Select(t => t.Hwnd).Prepend(_callWindow).Where(h => h != IntPtr.Zero).Distinct().ToList();
        var names = CallReader.NamesFor(action);
        var done = await Task.Run(() =>
        {
            foreach (var hwnd in targets)
            {
                try
                {
                    var button = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, ButtonNamed(names));
                    if (button is not null && Press(button))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (IsUiaFailure(ex))
                {
                }
            }

            return false;
        });

        DebugLog.Write($"Call action {action}: {(done ? "pressed" : "button not found")}");
        CheckSoon();
        return done;
    }

    /// <summary>Brings WhatsApp's call window (or WhatsApp) to the front.</summary>
    public void ShowApp()
    {
        var window = _callWindow != IntPtr.Zero ? _callWindow : Targets(includeMain: true).Select(t => t.Hwnd).FirstOrDefault();
        if (window != IntPtr.Zero)
        {
            WindowFinder.Focus(window);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo("whatsapp:") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ Looking

    private void Tick()
    {
        _timer.Interval = TimeSpan.FromSeconds(1);
        if (_scanning)
        {
            return;
        }

        var cost = Stopwatch.StartNew();
        RefreshProcesses();
        var tProcesses = cost.Elapsed.TotalMilliseconds;
        if (_pids.Count == 0)
        {
            Report(null, null);
            return;
        }

        DateTimeOffset? micSince = null;
        var mic = !_testMode && MicrophoneInUse(out micSince);
        var tMic = cost.Elapsed.TotalMilliseconds;
        var sound = AudioSessions.AnyActive(_pids);
        var tSound = cost.Elapsed.TotalMilliseconds;
        var windows = Targets(includeMain: false);
        var tWindows = cost.Elapsed.TotalMilliseconds;
        _costs[0] += tProcesses;
        _costs[1] += tMic - tProcesses;
        _costs[2] += tSound - tMic;
        _costs[3] += tWindows - tSound;
        if (++_costTicks == 30)
        {
            DebugLog.Write($"Call watcher cost per check (ms): processes {_costs[0] / 30:N2}, mic {_costs[1] / 30:N2}, sound {_costs[2] / 30:N2}, windows {_costs[3] / 30:N2}");
            Array.Clear(_costs);
            _costTicks = 0;
        }
        var signals = $"mic={mic} sound={sound} windows={windows.Count}";
        if (signals != _lastSignals)
        {
            _lastSignals = signals;
            DebugLog.Write($"Call signals: {signals}");
        }

        var busy = mic || sound || Current is not null;
        if (!busy && windows.Count == 0)
        {
            Report(null, null);
            return;
        }

        // The main window can be big, so look inside it at most every 3 s, and only while WhatsApp is
        // making sound or using the microphone (a call in the main window always does one of them).
        // Not at all while the call's controls are already known.
        var includeMain = busy && _container is null && DateTime.UtcNow - _mainScannedAt >= MainScanInterval;
        var targets = includeMain ? Targets(includeMain: true) : windows;
        if (includeMain)
        {
            _mainScannedAt = DateTime.UtcNow;
        }

        _scanning = true;
        Task.Run(() => Scan(targets, mic)).ContinueWith(t =>
        {
            _scanning = false;
            if (t.IsFaulted)
            {
                return;
            }

            var (snapshot, window) = t.Result;
            if (snapshot is null && !includeMain && Current is { } current && busy)
            {
                return; // the call may be in the main window, which wasn't read this time
            }

            if (window != IntPtr.Zero)
            {
                _callWindow = window;
            }

            Report(snapshot, mic ? micSince : null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private (CallSnapshot? Snapshot, IntPtr Window) Scan(IReadOnlyList<(IntPtr Hwnd, string Title, bool IsToast)> targets, bool mic)
    {
        var watch = Stopwatch.StartNew();

        // Quick path: the call's controls are where they were a second ago.
        if (_container is { } known)
        {
            try
            {
                var items = Items(known.Element);
                if (items.Any(i => i.IsButton && CallReader.AllButtonNames.Contains(i.Name.Trim(), StringComparer.OrdinalIgnoreCase)) &&
                    CallReader.Read(items, known.IsToast ? null : known.Title, mic) is { } same)
                {
                    Dump(known.Title, items);
                    return (same, _callWindow);
                }
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
            }

            _container = null; // gone or changed: look again from scratch
        }

        var names = CallReader.AllButtonNames.ToArray();
        foreach (var (hwnd, title, isToast) in targets)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var button = root.FindFirst(TreeScope.Descendants, ButtonNamed(names));
                if (button is null)
                {
                    continue;
                }

                // Read the part of the window that holds the call: the whole window for a call window or
                // a notification, the call bar around the buttons inside the main window.
                var container = isToast || !IsBig(hwnd) ? root : Ancestor(button, 3) ?? root;
                var items = Items(container);
                if (isToast && !items.Any(i => i.Name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) &&
                    !title.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // another app's notification
                }

                Dump(title, items);
                var snapshot = CallReader.Read(items, isToast ? null : title, mic);
                if (snapshot is not null)
                {
                    _container = (container, title, isToast);
                    DebugLog.Write($"Call: {snapshot.State} '{snapshot.Caller}' muted={snapshot.IsMuted} duration={snapshot.Duration} in {watch.ElapsedMilliseconds} ms");
                    return (snapshot, isToast ? IntPtr.Zero : hwnd);
                }
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
            }
        }

        // Microphone open but no call controls found: still a call, just one Nexus can't control.
        return (mic ? CallReader.Read([], null, micInUse: true) : null, IntPtr.Zero);
    }

    private void Report(CallSnapshot? snapshot, DateTimeOffset? micSince)
    {
        if (snapshot is not null && snapshot.Caller is null && Current?.Caller is { } known)
        {
            snapshot = snapshot with { Caller = known }; // keep the name we already know
        }

        if (Equals(snapshot, Current))
        {
            return;
        }

        if (snapshot is null)
        {
            _callWindow = IntPtr.Zero;
            _container = null;
        }

        Current = snapshot;
        Changed?.Invoke(snapshot, micSince);
    }

    /// <summary>For tuning to new WhatsApp versions: what the call UI exposes, logged once per change (NEXUS_DEBUG=1).</summary>
    private void Dump(string title, List<CallUiItem> items)
    {
        var dump = $"'{title}': " + string.Join(" | ", items.Select(i => (i.IsButton ? "[" : "") + i.Name + (i.IsButton ? "]" : "")));
        if (dump != _lastDump)
        {
            _lastDump = dump;
            DebugLog.Write($"Call UI {dump}");
        }
    }

    // ------------------------------------------------------------------ Windows

    private void RefreshProcesses()
    {
        // Listing processes is the costliest check, and WhatsApp rarely starts and gets a call within
        // the same few seconds, so look every 10 s.
        if (DateTime.UtcNow - _pidsCheckedAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _pidsCheckedAt = DateTime.UtcNow;
        var pids = new HashSet<int>();
        foreach (var name in _apps)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
        }

        _pids = pids;
    }

    /// <summary>
    /// WhatsApp's windows on screen, and call notifications. Without <paramref name="includeMain"/>,
    /// only small windows (a call window) and notifications: the main chat window is skipped.
    /// </summary>
    private List<(IntPtr Hwnd, string Title, bool IsToast)> Targets(bool includeMain)
    {
        var found = new List<(IntPtr, string, bool)>();
        var pids = _pids;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || !Win32.GetWindowRect(hwnd, out var r) || r.Width < 120 || r.Height < 48 || r.Left <= -10000)
            {
                return true;
            }

            var title = Win32.WindowText(hwnd);
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains((int)pid))
            {
                if (includeMain || !IsMainWindow(title, r))
                {
                    found.Add((hwnd, title, false));
                }
            }
            else if (title == "New notification" && !_testMode)
            {
                found.Add((hwnd, title, true)); // a Windows notification, possibly WhatsApp's call alert
            }

            return true;
        }, IntPtr.Zero);

        // Call windows and notifications first: they are small and quick to read.
        return found.OrderBy(t => t.Item3 ? 0 : IsBig(t.Item1) ? 2 : 1).ToList();
    }

    private static bool IsMainWindow(string title, Win32.RECT r) =>
        string.Equals(title, "WhatsApp", StringComparison.OrdinalIgnoreCase) && r.Width >= 700 && r.Height >= 500;

    private static bool IsBig(IntPtr hwnd) => Win32.GetWindowRect(hwnd, out var r) && r.Width >= 700 && r.Height >= 500;

    // ------------------------------------------------------------------ Microphone

    /// <summary>
    /// Windows records, for its privacy settings, when each app last opened the microphone. While it
    /// is still open the stop time is zero; the start time is when the call connected.
    /// </summary>
    private List<string> _micKeys = [];
    private DateTime _micKeysAt;

    private bool MicrophoneInUse(out DateTimeOffset? since)
    {
        since = null;
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(MicrophoneStore);
            if (store is null)
            {
                return false;
            }

            // Which records belong to WhatsApp changes only when it is installed or moved: find them once a minute.
            if (DateTime.UtcNow - _micKeysAt > TimeSpan.FromMinutes(1))
            {
                _micKeysAt = DateTime.UtcNow;
                _micKeys = CandidateKeys(store).ToList();
            }

            foreach (var path in _micKeys)
            {
                using var key = store.OpenSubKey(path);
                if (key is not null)
                {
                    if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
                        key.GetValue("LastUsedTimeStart") is long start && start > 0)
                    {
                        since = DateTime.FromFileTimeUtc(start);
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }

        return false;
    }

    /// <summary>Paths, under the microphone store, of WhatsApp's usage records (packaged and not).</summary>
    private static IEnumerable<string> CandidateKeys(RegistryKey store)
    {
        foreach (var name in store.GetSubKeyNames())
        {
            if (name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                yield return name;
            }
        }

        using var nonPackaged = store.OpenSubKey("NonPackaged");
        if (nonPackaged is null)
        {
            yield break;
        }

        foreach (var name in nonPackaged.GetSubKeyNames())
        {
            if (name.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                yield return @"NonPackaged\" + name;
            }
        }
    }

    // ------------------------------------------------------------------ UI Automation

    private static Condition ButtonNamed(IEnumerable<string> names) => new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new OrCondition(names
            .Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n, PropertyConditionFlags.IgnoreCase))
            .Append(Condition.FalseCondition) // OrCondition needs at least two
            .ToArray()));

    private static readonly Condition ButtonOrText = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

    private static List<CallUiItem> Items(AutomationElement container)
    {
        var items = new List<CallUiItem>();
        foreach (AutomationElement e in container.FindAll(TreeScope.Descendants, ButtonOrText))
        {
            if (items.Count >= 120)
            {
                break;
            }

            try
            {
                var name = e.Current.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    items.Add(new CallUiItem(e.Current.ControlType == ControlType.Button, name));
                }
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
            }
        }

        return items;
    }

    private static AutomationElement? Ancestor(AutomationElement element, int levels)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var i = 0; i < levels; i++)
        {
            var parent = walker.GetParent(current);
            if (parent is null || parent == AutomationElement.RootElement)
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static bool Press(AutomationElement button)
    {
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return true;
        }

        if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return true;
        }

        return false;
    }

    private static bool IsUiaFailure(Exception ex) =>
        ex is ElementNotAvailableException or ElementNotEnabledException or COMException or InvalidOperationException or TimeoutException or ArgumentException;
}
