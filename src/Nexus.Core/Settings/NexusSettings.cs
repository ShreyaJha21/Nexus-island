using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core.Settings;

public enum Placement
{
    /// <summary>Centered, floating just above the taskbar. Falls back to top-center when that is not possible.</summary>
    AboveTaskbar,
    TopCenter,
}

/// <summary>Everything Nexus remembers. Stored as JSON in %AppData%\Nexus; no account, no cloud.</summary>
public sealed class NexusSettings
{
    public Placement Placement { get; set; } = Placement.AboveTaskbar;

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
