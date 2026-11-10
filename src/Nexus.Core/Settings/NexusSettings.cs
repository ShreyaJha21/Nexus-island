using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core.Settings;

/// <summary>Where the island lives. Also used for the spots it may move to when its own is taken.</summary>
public enum Placement
{
    /// <summary>Bottom centre: floating just above the taskbar. Falls back to top-center when that is not possible.</summary>
    AboveTaskbar,
    /// <summary>Hanging flush from the top edge of the screen.</summary>
    TopCenter,
    /// <summary>Floating above the taskbar, at the left edge of the screen.</summary>
    BottomLeft,
    /// <summary>Floating above the taskbar, at the right edge of the screen.</summary>
    BottomRight,
    /// <summary>Tucked into free space inside the taskbar itself, so it never covers an app.</summary>
    Taskbar,
    /// <summary>Wherever the user dragged it (<see cref="PositioningSettings.CustomX"/>, CustomY).</summary>
    Custom,
}

/// <summary>What the island does when the app in front has controls where the island sits.</summary>
public enum PositionMode
{
    /// <summary>Stay put, whatever is underneath.</summary>
    Fixed,
    /// <summary>Move to a clear spot, and come back when the conflict is gone.</summary>
    Smart,
    /// <summary>Hide while a window covers the island's spot; come back on the desktop.</summary>
    AutoHide,
}

/// <summary>Everything Nexus remembers. Stored as JSON in %AppData%\Nexus; no account, no cloud.</summary>
public sealed class NexusSettings
{
    /// <summary>The island's preferred position: bottom centre, just above the taskbar, by default.</summary>
    public Placement Placement { get; set; } = Placement.AboveTaskbar;

    /// <summary>How the island keeps out of the way of the app in front.</summary>
    public PositioningSettings Positioning { get; set; } = new();

    /// <summary>Hidden with the shortcut; remembered across restarts.</summary>
    public bool Hidden { get; set; }

    /// <summary>"Hide for 1 hour" / "Hide until tomorrow".</summary>
    public DateTimeOffset? HiddenUntil { get; set; }

    public FocusSettings Focus { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();
    public MusicSettings Music { get; set; } = new();

    public string? PinnedModeId { get; set; }

    /// <summary>Global show/hide shortcut, e.g. "Ctrl+Alt+Space". If another app owns it, pick a different one here.</summary>
    public string HideShortcut { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Start or stop a focus session with the default preset.</summary>
    public string FocusShortcut { get; set; } = "Ctrl+Alt+F";

    /// <summary>Use the Windows accent color instead of Fluent's default blue.</summary>
    public bool UseSystemAccent { get; set; } = true;
}

/// <summary>Smart workspace awareness: where the island may go, and when it stays small.</summary>
public sealed class PositioningSettings
{
    public PositionMode Mode { get; set; } = PositionMode.Smart;

    /// <summary>Drag the island anywhere; it remembers the spot (Placement becomes Custom).</summary>
    public bool Draggable { get; set; } = true;

    /// <summary>Never grow on its own: activities stay a thin line until you point at it. "Needs you" still opens.</summary>
    public bool AlwaysCompact { get; set; }

    /// <summary>
    /// Smart mode: never move out of the way of a web browser. Pages scroll under the island and their
    /// controls change with every site, so moving would just make it jump around. Figma in a browser
    /// tab still follows its own rule.
    /// </summary>
    public bool StayInBrowsers { get; set; } = true;

    /// <summary>
    /// Smart mode: ask Windows (UI Automation) which buttons, toolbars, text boxes and scroll bars lie
    /// under the island. Off: only the per-app rules in <see cref="Apps"/> count.
    /// </summary>
    public bool DetectControls { get; set; } = true;

    /// <summary>Smart mode: the spots to try, in order, when the preferred one is taken.</summary>
    public List<Placement> SafeSpots { get; set; } = [Placement.BottomRight, Placement.BottomLeft, Placement.Taskbar];

    /// <summary>Custom placement: the island's centre as a fraction (0 to 1) of the screen's work area.</summary>
    public double CustomX { get; set; } = 0.5;
    public double CustomY { get; set; } = 0.9;

    /// <summary>Custom placement: the screen it was dropped on (a Windows display name), or null for the main one.</summary>
    public string? CustomMonitor { get; set; }

    /// <summary>Times of day to stay compact, e.g. "09:30-12:00". A range may cross midnight.</summary>
    public List<string> CompactHours { get; set; } = [];

    /// <summary>
    /// Rules per app, keyed by process name ("Figma") or by the name at the end of a browser tab's
    /// title. Figma keeps its toolbar at the bottom centre, so that spot is off limits there.
    /// </summary>
    public Dictionary<string, AppRule> Apps { get; set; } = new()
    {
        ["Figma"] = new AppRule { Avoid = [Placement.AboveTaskbar], MoveTo = Placement.BottomRight },
    };
}

/// <summary>What the island does while one particular app is in front (Smart mode).</summary>
public sealed class AppRule
{
    /// <summary>Spots where this app keeps controls. When set, the other spots count as clear without asking Windows.</summary>
    public List<Placement> Avoid { get; set; } = [];

    /// <summary>The spot to try first when the island has to move.</summary>
    public Placement? MoveTo { get; set; }

    /// <summary>Stay compact while this app is in front.</summary>
    public bool Compact { get; set; }

    /// <summary>Hide while this app is in front. "Needs you" still shows.</summary>
    public bool Hide { get; set; }
}

public sealed class FocusSettings
{
    public int CustomFocusMinutes { get; set; } = 45;
    public int CustomBreakMinutes { get; set; } = 15;
    public string DefaultPreset { get; set; } = "25/5";
    public bool AutoStartBreak { get; set; }
    public int DailyGoal { get; set; } = 4;
    public bool Chime { get; set; } = true;

    public DateOnly SessionsDate { get; set; }
    public int SessionsToday { get; set; }
}

public sealed class AgentSettings
{
    public int Port { get; set; }
    public string Token { get; set; } = "";
}

public sealed class MusicSettings
{
    /// <summary>App ids (or parts of them) that never expand the island on a track change.</summary>
    public List<string> ExcludedApps { get; set; } = [];
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexus", "settings.json");
    }

    public string Path { get; }

    public NexusSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<NexusSettings>(File.ReadAllText(Path), Options) ?? new NexusSettings();
            }
        }
        catch (JsonException)
        {
            // A broken file should never stop the island from starting. Keep a copy and start fresh.
            File.Copy(Path, Path + ".broken", overwrite: true);
        }

        return new NexusSettings();
    }

    public void Save(NexusSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
