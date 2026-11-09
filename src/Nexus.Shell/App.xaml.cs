using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Nexus.Core.Agent;
using Nexus.Core.Focus;
using Nexus.Core.Settings;
using Nexus.Modes;
using Nexus.Modes.Agent;
using Nexus.Modes.Focus;
using Nexus.Modes.Music;
using Nexus.Shell.Island;
using Nexus.Shell.Native;
using Nexus.Shell.Setup;

namespace Nexus.Shell;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsStore _store = null!;
    private NexusSettings _settings = null!;
    private AgentServer? _server;
    private IslandWindow _window = null!;
    private IslandController _controller = null!;
    private HotkeyManager? _hotkeys;
    private FullscreenWatcher? _fullscreen;
    private TrayIcon? _tray;
    private FocusMode _focus = null!;
    private AgentMode _agent = null!;
    private MusicMode _music = null!;
    private HookSetupWindow? _setupWindow;
    private bool _hideShortcutAvailable;
    private bool _focusShortcutAvailable;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\NexusIsland.Nexus", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // Performance budget (PRD): memory under 100 MB. A transparent always-on-top WPF window rendered
        // on the GPU keeps a Direct3D device and readback surfaces alive (~200 MB private on a typical
        // laptop). The island is small and only animates briefly, so software rendering is cheaper overall
        // (~70 MB). Set NEXUS_RENDER=hardware to compare.
        if (Environment.GetEnvironmentVariable("NEXUS_RENDER") != "hardware")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        _store = new SettingsStore();
        _settings = _store.Load();
        EnsureAgentCredentials();
        var theme = new SystemTheme(_settings.UseSystemAccent);

        _focus = new FocusMode(_settings, Save);
        _music = new MusicMode(_settings);
        _agent = new AgentMode(cwd => WindowFinder.FocusTerminal(cwd), OpenAgentSetup);
        StartAgentServer();

        _window = new IslandWindow { Placement = _settings.Placement };
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        theme.SetBackdrop(_window.BackdropKind);

        _controller = new IslandController(
            _window,
            [_focus, _agent, _music],
            _settings,
            Save,
            agentConnected: IsAgentConnected,
            openAgentSetup: OpenAgentSetup,
            openSettingsFile: OpenSettingsFile);

        _hotkeys = new HotkeyManager(HwndSource.FromHwnd(handle));
        var hideShortcut = Shortcut.Parse(_settings.HideShortcut);
        var focusShortcut = Shortcut.Parse(_settings.FocusShortcut);
        _hideShortcutAvailable = _hotkeys.TryRegister(hideShortcut, _controller.ToggleHidden);
        _focusShortcutAvailable = _hotkeys.TryRegister(focusShortcut, _focus.ToggleDefault);
        _controller.SetHideShortcut(_hideShortcutAvailable ? hideShortcut!.Text : null);

        _tray = new TrayIcon("Nexus");
        _tray.LeftClick += _controller.OpenFromTray;
        _tray.RightClick += ShowTrayMenu;
        if (!_hideShortcutAvailable)
        {
            // PRD F6: if another app owns the shortcut, say so and ask for a different one.
            _tray.ShowBalloon(hideShortcut is null ? "Hide shortcut not recognised" : $"{hideShortcut.Text} is taken",
                "Another app already uses this shortcut. Choose a different HideShortcut in Nexus settings (tray menu > Open settings file), then restart Nexus.");
        }

        _fullscreen = new FullscreenWatcher(() => _window.Handle);
        _fullscreen.Changed += _controller.SetFullscreen;
        _fullscreen.ForegroundChanged += _window.EnsureTopmost;

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(_controller.Reposition);
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        _controller.Start();
        _fullscreen.Check();

        await _music.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fullscreen?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _server?.Dispose();
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void Save() => _store.Save(_settings);

    private void EnsureAgentCredentials()
    {
        var changed = false;
        if (_settings.Agent.Port is <= 0 or > 65535)
        {
            _settings.Agent.Port = AgentServer.PickFreePort();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.Agent.Token))
        {
            _settings.Agent.Token = AgentServer.NewToken();
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    private void StartAgentServer()
    {
        _server = new AgentServer(_settings.Agent.Port, _settings.Agent.Token);
        if (!_server.TryStart())
        {
            // The port was taken by something else. Pick a new one; the hooks will need updating,
            // which the launcher and the setup screen both point out.
            _server.Dispose();
            _settings.Agent.Port = AgentServer.PickFreePort();
            Save();
            _server = new AgentServer(_settings.Agent.Port, _settings.Agent.Token);
            _server.TryStart();
        }

        _server.EventReceived += ev => Dispatcher.InvokeAsync(() =>
        {
            var watch = Stopwatch.StartNew();
            _agent.Apply(ev);
            DebugLog.Write($"Agent event {ev.EventName} handled in {watch.Elapsed.TotalMilliseconds:N1} ms");
        });
    }

    private bool IsAgentConnected()
    {
        if (_server is null)
        {
            return false;
        }

        try
        {
            return HookInstaller.IsInstalled(_server.EventsUrl);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OpenAgentSetup()
    {
        if (_server is null)
        {
            return;
        }

        if (_setupWindow is { IsLoaded: true })
        {
            _setupWindow.Activate();
            return;
        }

        _setupWindow = new HookSetupWindow(_server.EventsUrl, _settings.Agent.Token);
        _setupWindow.Closed += (_, _) => _setupWindow = null;
        _setupWindow.Show();
        _setupWindow.Activate();
    }

    private void OpenSettingsFile()
    {
        Save();
        try
        {
            Process.Start(new ProcessStartInfo(_store.Path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.Path}\"") { UseShellExecute = true });
        }
    }

    private void ShowTrayMenu()
    {
        var hidden = _controller.IsHiddenByUser;
        var menu = new ContextMenu();

        menu.Items.Add(IslandController.Item(hidden ? "Show Nexus" : "Hide Nexus", hidden ? "chevronUp" : "eyeOff",
            _controller.ToggleHidden, gesture: _hideShortcutAvailable ? _settings.HideShortcut : "shortcut taken"));
        menu.Items.Add(IslandController.Item("Hide for 1 hour", "eyeOff",
            () => _controller.Hide(DateTimeOffset.Now.AddHours(1))));
        menu.Items.Add(IslandController.Item("Hide until tomorrow", "moon",
            () => _controller.Hide(DateTime.Today.AddDays(1).AddHours(6))));
        menu.Items.Add(new Separator());

        if (_focus.Phase == FocusPhase.Idle)
        {
            menu.Items.Add(IslandController.Item("Focus 25 / 5", "timer", () => _focus.Start(FocusPreset.Short),
                gesture: _focusShortcutAvailable ? _settings.FocusShortcut : null));
            menu.Items.Add(IslandController.Item("Focus 50 / 10", "timer", () => _focus.Start(FocusPreset.Long)));
            var custom = _focus.CustomPreset;
            menu.Items.Add(IslandController.Item($"Focus {custom.FocusMinutes} / {custom.BreakMinutes} (custom)", "timer",
                () => _focus.Start(custom)));
        }
        else
        {
            menu.Items.Add(IslandController.Item("Stop focus", "dismiss", _focus.Stop,
                gesture: _focusShortcutAvailable ? _settings.FocusShortcut : null));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(IslandController.Item(IsAgentConnected() ? "Claude Code: connected" : "Connect Claude Code…", "bot", OpenAgentSetup));

        var top = _settings.Placement == Placement.TopCenter;
        menu.Items.Add(IslandController.Item(top ? "Move above the taskbar" : "Move to top center", top ? "chevronDown" : "chevronUp", () =>
        {
            _settings.Placement = top ? Placement.AboveTaskbar : Placement.TopCenter;
            _window.Placement = _settings.Placement;
            Save();
            _window.Reposition();
        }));

        var startup = StartupRegistration.IsEnabled;
        menu.Items.Add(IslandController.Item("Start with Windows", startup ? "check" : "add", () => StartupRegistration.Set(!startup)));
        menu.Items.Add(IslandController.Item("Open settings file", "settings", OpenSettingsFile));
        menu.Items.Add(new Separator());
        menu.Items.Add(IslandController.Item("Quit Nexus", "dismiss", Shutdown));

        IslandController.OpenMenu(menu, null);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The taskbar moved, resized or changed auto-hide.
        if (e.PropertyName is nameof(SystemParameters.WorkArea) or nameof(SystemParameters.PrimaryScreenHeight))
        {
            Dispatcher.InvokeAsync(_controller.Reposition);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An always-on overlay should not vanish because of one bad event. Log it and keep going.
        try
        {
            var dir = Path.GetDirectoryName(_store?.Path ?? Path.GetTempPath())!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "nexus.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }

        e.Handled = true;
    }
}
