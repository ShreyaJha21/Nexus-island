using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Nexus.Core.Agent;
using Nexus.Shell.Native;

namespace Nexus.Shell.Setup;

/// <summary>
/// Claude Code settings are never changed silently: this screen shows the exact hook entries,
/// writes them only on "Add hooks", keeps a backup, and offers to remove them again.
/// </summary>
public partial class HookSetupWindow : Window
{
    private readonly string _eventsUrl;
    private readonly string _token;
    private readonly string _settingsPath = HookInstaller.DefaultSettingsPath;

    public HookSetupWindow(string eventsUrl, string token)
    {
        _eventsUrl = eventsUrl;
        _token = token;
        InitializeComponent();

        PathText.Text = $"File: {_settingsPath}  ·  a backup is saved next to it as settings.json.nexus-backup";
        Preview.Text = HookInstaller.PreviewJson(eventsUrl, token);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
        UpdateState();
    }

    private void UpdateState()
    {
        bool installed, any;
        try
        {
            installed = HookInstaller.IsInstalled(_eventsUrl, _settingsPath);
            any = HookInstaller.HasAnyNexusHook(_settingsPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            StatusText.Text = $"Your Claude Code settings file could not be read ({ex.Message}). Fix it, then try again.";
            ApproveButton.IsEnabled = false;
            RemoveButton.Visibility = Visibility.Collapsed;
            return;
        }

        RemoveButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ApproveButton.Content = installed ? "Reinstall hooks" : any ? "Update hooks" : "Add hooks";
        StatusText.Text = installed
            ? "Connected. New Claude Code sessions will report to Nexus."
            : any
                ? "Nexus hooks from an earlier setup point to an old address. Update them to reconnect."
                : "Not connected yet. Claude Code sessions started after you add the hooks will appear on the island.";
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        try
        {
            HookInstaller.Install(_eventsUrl, _token, _settingsPath);
            StatusText.Text = "Hooks added. Start a new Claude Code session (or run /hooks in an open one) to connect.";
            ApproveButton.Content = "Done";
            ApproveButton.Click -= OnApprove;
            ApproveButton.Click += (_, _) => Close();
            RemoveButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not update the settings file: {ex.Message}";
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            HookInstaller.Uninstall(_settingsPath);
            UpdateState();
            StatusText.Text = "Nexus hooks removed. Your other Claude Code settings were left as they were.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not update the settings file: {ex.Message}";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
