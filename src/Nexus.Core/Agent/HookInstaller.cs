using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Core.Agent;

/// <summary>
/// Builds and writes the Claude Code hook entries that post lifecycle events to Nexus.
/// Nothing is written silently: the shell shows <see cref="PreviewJson"/> first and only
/// calls <see cref="Install"/> after the user approves. Every entry Nexus adds carries the
/// Nexus URL, so <see cref="Uninstall"/> removes exactly those and nothing else.
/// </summary>
public static class HookInstaller
{
    /// <summary>Events Nexus listens to. Matcher-based events use "*" to see every tool.</summary>
    public static readonly IReadOnlyList<(string Event, bool UsesMatcher)> Events =
    [
        ("SessionStart", false),
        ("UserPromptSubmit", false),
        ("PreToolUse", true),
        ("PostToolUse", true),
        ("PostToolUseFailure", true),
        ("PermissionRequest", true),
        ("Notification", false),
        ("Stop", false),
        ("StopFailure", false),
        ("SessionEnd", false),
    ];

    /// <summary>A short timeout, so a closed Nexus never slows the agent down.</summary>
    private const int TimeoutSeconds = 2;

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static JsonObject BuildHook(string eventsUrl, string token) => new()
    {
        ["type"] = "http",
        ["url"] = eventsUrl,
        ["timeout"] = TimeoutSeconds,
        ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" },
    };

    /// <summary>Exactly the "hooks" block Nexus will merge in, for the approval screen.</summary>
    public static string PreviewJson(string eventsUrl, string token)
    {
        var hooks = new JsonObject();
        foreach (var (name, usesMatcher) in Events)
        {
            hooks[name] = new JsonArray(Group(eventsUrl, token, usesMatcher));
        }

        return new JsonObject { ["hooks"] = hooks }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool IsInstalled(string eventsUrl, string? settingsPath = null)
    {
        var root = Load(settingsPath ?? DefaultSettingsPath);
        return root?["hooks"] is JsonObject hooks &&
               Events.All(e => hooks[e.Event] is JsonArray groups && groups.Any(g => IsNexusGroup(g, eventsUrl)));
    }

    /// <summary>True if any Nexus hook exists, even one pointing at an old port or token.</summary>
    public static bool HasAnyNexusHook(string? settingsPath = null)
    {
        var root = Load(settingsPath ?? DefaultSettingsPath);
        return root?["hooks"] is JsonObject hooks &&
               hooks.Any(kv => kv.Value is JsonArray groups && groups.Any(g => IsNexusGroup(g, null)));
    }

    /// <summary>Merges the Nexus hooks into the user's settings, replacing older Nexus entries.</summary>
    public static void Install(string eventsUrl, string token, string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var root = Load(path) ?? new JsonObject();
        RemoveNexusGroups(root);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (name, usesMatcher) in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[name] = groups;
            }

            groups.Add(Group(eventsUrl, token, usesMatcher));
        }

        Save(path, root);
    }

    public static void Uninstall(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var root = Load(path);
        if (root is null)
        {
            return;
        }

        RemoveNexusGroups(root);
        Save(path, root);
    }

    private static JsonObject Group(string eventsUrl, string token, bool usesMatcher)
    {
        var group = new JsonObject();
        if (usesMatcher)
        {
            group["matcher"] = "*";
        }

        group["hooks"] = new JsonArray(BuildHook(eventsUrl, token));
        return group;
    }

    private static void RemoveNexusGroups(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (var name in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups)
            {
                continue;
            }

            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (IsNexusGroup(groups[i], null))
                {
                    groups.RemoveAt(i);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
    }

    /// <summary>A group is Nexus's if every hook in it posts to a Nexus events URL.</summary>
    private static bool IsNexusGroup(JsonNode? group, string? exactUrl)
    {
        if (group?["hooks"] is not JsonArray handlers || handlers.Count == 0)
        {
            return false;
        }

        return handlers.All(h =>
            h?["type"]?.GetValue<string>() == "http" &&
            h["url"]?.GetValue<string>() is { } url &&
            (exactUrl is null
                ? url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && url.EndsWith(AgentServer.EventsPath, StringComparison.Ordinal)
                : url == exactUrl));
    }

    private static JsonObject? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object.");
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".nexus-backup", overwrite: true);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
