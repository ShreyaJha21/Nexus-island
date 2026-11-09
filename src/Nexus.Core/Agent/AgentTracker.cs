using System.Text.Json.Nodes;

namespace Nexus.Core.Agent;

public enum AgentState
{
    /// <summary>Session exists but nothing is happening. Not shown.</summary>
    Idle,
    Working,
    /// <summary>Waiting on a permission prompt or for the user's next message.</summary>
    NeedsYou,
    Done,
    Failed,
}

public enum NeedsYouKind
{
    Permission,
    Input,
}

public sealed class AgentSession
{
    public required string Id { get; init; }
    public string Agent { get; set; } = "Claude";
    public string Project { get; set; } = "";
    public string? Cwd { get; set; }
    public AgentState State { get; set; } = AgentState.Idle;
    public NeedsYouKind NeedsYouKind { get; set; }

    /// <summary>Short, safe description: "Editing auth.ts", "Running tests".</summary>
    public string Detail { get; set; } = "";

    /// <summary>The tool waiting for permission, e.g. "Bash".</summary>
    public string? PendingTool { get; set; }

    /// <summary>A one-line preview of the command awaiting permission, truncated.</summary>
    public string? PendingCommand { get; set; }

    public DateTimeOffset? TurnStartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public TimeSpan? TurnDuration { get; set; }

    /// <summary>While set and in the future, a needs-you session does not expand the island.</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }

    public bool IsVisible => State is not AgentState.Idle;
}

/// <summary>
/// Turns the stream of agent hook events into a per-session state machine:
/// Working, Needs you, Done (fades after 10 s) and Failed (stays until dismissed).
/// </summary>
public sealed class AgentTracker
{
    public static readonly TimeSpan DoneVisibleFor = TimeSpan.FromSeconds(10);
    private const int MaxCommandPreview = 90;

    private readonly Dictionary<string, AgentSession> _sessions = new();

    /// <summary>Raised after any event changes state. The session is the one that changed.</summary>
    public event Action<AgentSession>? Changed;

    /// <summary>Raised when a session newly needs the user. The island uses this to expand.</summary>
    public event Action<AgentSession>? NeedsYouRaised;

    public IReadOnlyCollection<AgentSession> Sessions => _sessions.Values;

    public IEnumerable<AgentSession> VisibleSessions => _sessions.Values
        .Where(s => s.IsVisible)
        .OrderBy(s => s.State switch
        {
            AgentState.NeedsYou => 0,
            AgentState.Failed => 1,
            AgentState.Working => 2,
            _ => 3,
        })
        .ThenByDescending(s => s.UpdatedAt);

    /// <summary>The last finished turn, for the launcher's "recent" row.</summary>
    public AgentSession? LastFinished { get; private set; }

    public void Apply(AgentEvent e, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(e.SessionId, out var s))
        {
            if (e.EventName == "SessionEnd")
            {
                return;
            }

            s = new AgentSession { Id = e.SessionId };
            _sessions[e.SessionId] = s;
        }

        s.Agent = e.Agent;
        if (!string.IsNullOrWhiteSpace(e.Cwd))
        {
            s.Cwd = e.Cwd;
            s.Project = ProjectName(e.Cwd);
        }

        var wasNeedsYou = s.State == AgentState.NeedsYou;

        switch (e.EventName)
        {
            case "SessionStart":
                s.State = AgentState.Idle;
                break;

            case "UserPromptSubmit":
                s.State = AgentState.Working;
                s.Detail = "Thinking";
                s.TurnStartedAt = now;
                s.FinishedAt = null;
                ClearPending(s);
                break;

            case "PreToolUse":
                s.State = AgentState.Working;
                s.Detail = Describe(e.ToolName, e.ToolInput);
                s.TurnStartedAt ??= now;
                s.PendingTool = e.ToolName;
                s.PendingCommand = CommandPreview(e.ToolName, e.ToolInput);
                break;

            case "PostToolUse":
            case "PostToolUseFailure":
            case "PermissionDenied":
                // The tool ran (or was refused), so any permission prompt has been handled.
                if (s.State is AgentState.NeedsYou or AgentState.Idle or AgentState.Done)
                {
                    s.State = AgentState.Working;
                }
                ClearPending(s);
                break;

            case "PermissionRequest":
                SetNeedsYou(s, NeedsYouKind.Permission, e.ToolName ?? s.PendingTool,
                    CommandPreview(e.ToolName, e.ToolInput) ?? s.PendingCommand, now);
                break;

            case "Notification":
                if (e.NotificationType == "permission_prompt" ||
                    (e.NotificationType is null && Contains(e.Message, "permission")))
                {
                    SetNeedsYou(s, NeedsYouKind.Permission, ToolFromMessage(e.Message) ?? s.PendingTool, s.PendingCommand, now);
                }
                else if (e.NotificationType is "idle_prompt" or "agent_needs_input" or "elicitation_dialog" ||
                         (e.NotificationType is null && Contains(e.Message, "waiting for your input")))
                {
                    SetNeedsYou(s, NeedsYouKind.Input, null, null, now);
                }
                break;

            case "Stop":
                s.State = AgentState.Done;
                s.FinishedAt = now;
                s.TurnDuration = s.TurnStartedAt is { } started ? now - started : null;
                s.Detail = FinishedText(s.TurnDuration);
                s.TurnStartedAt = null;
                ClearPending(s);
                LastFinished = s;
                break;

            case "StopFailure":
                s.State = AgentState.Failed;
                s.FinishedAt = now;
                s.Detail = FailureText(e.Error);
                s.TurnStartedAt = null;
                ClearPending(s);
                break;

            case "SessionEnd":
                _sessions.Remove(s.Id);
                Changed?.Invoke(s);
                return;

            default:
                // Unknown or uninteresting events still prove the agent is connected.
                break;
        }

        s.UpdatedAt = now;
        Changed?.Invoke(s);

        if (s.State == AgentState.NeedsYou && !wasNeedsYou)
        {
            NeedsYouRaised?.Invoke(s);
        }
    }

    /// <summary>Hides finished turns after 10 s. Returns true if anything changed.</summary>
    public bool Expire(DateTimeOffset now)
    {
        var changed = false;
        foreach (var s in _sessions.Values)
        {
            if (s.State == AgentState.Done && s.FinishedAt is { } finished && now - finished >= DoneVisibleFor)
            {
                s.State = AgentState.Idle;
                changed = true;
            }
        }

        return changed;
    }

    public void Dismiss(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s) && s.State is AgentState.Failed or AgentState.Done or AgentState.NeedsYou)
        {
            s.State = AgentState.Idle;
            s.SnoozedUntil = null;
            Changed?.Invoke(s);
        }
    }

    public void Snooze(string sessionId, TimeSpan duration, DateTimeOffset now)
    {
        if (_sessions.TryGetValue(sessionId, out var s) && s.State == AgentState.NeedsYou)
        {
            s.SnoozedUntil = now + duration;
            Changed?.Invoke(s);
        }
    }

    /// <summary>Re-raises snoozed sessions whose snooze has run out. Returns true if any did.</summary>
    public bool WakeSnoozed(DateTimeOffset now)
    {
        var woke = false;
        foreach (var s in _sessions.Values)
        {
            if (s.SnoozedUntil is { } until && until <= now)
            {
                s.SnoozedUntil = null;
                if (s.State == AgentState.NeedsYou)
                {
                    woke = true;
                    Changed?.Invoke(s);
                    NeedsYouRaised?.Invoke(s);
                }
            }
        }

        return woke;
    }

    // ---- Text helpers. Nexus shows the tool and file name, never file contents or prompts. ----

    public static string Describe(string? tool, JsonObject? input)
    {
        var file = FileName(Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path"));
        return tool switch
        {
            "Edit" or "MultiEdit" => file is null ? "Editing a file" : $"Editing {file}",
            "Write" => file is null ? "Writing a file" : $"Writing {file}",
            "NotebookEdit" => file is null ? "Editing a notebook" : $"Editing {file}",
            "Read" => file is null ? "Reading" : $"Reading {file}",
            "Bash" or "PowerShell" => DescribeCommand(Str(input, "command")),
            "Grep" or "Glob" or "LS" => "Searching the code",
            "WebFetch" or "WebSearch" => "Browsing the web",
            "Task" or "Agent" => "Running a subagent",
            "TodoWrite" => "Planning",
            null or "" => "Working",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => $"Using {tool.Split("__").ElementAtOrDefault(1) ?? "a tool"}",
            _ => $"Using {tool}",
        };
    }

    public static string FinishedText(TimeSpan? duration) => duration is { } d
        ? $"Done in {FormatDuration(d)}"
        : "Done";

    public static string FormatDuration(TimeSpan d) => d.TotalMinutes < 1
        ? $"{Math.Max(1, (int)d.TotalSeconds)} s"
        : d.TotalHours < 1
            ? $"{(int)Math.Round(d.TotalMinutes)} min"
            : $"{(int)d.TotalHours} h {d.Minutes} m";

    private static string DescribeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Running a command";
        }

        var c = command.ToLowerInvariant();
        if (c.Contains("test") || c.Contains("pytest") || c.Contains("jest") || c.Contains("vitest"))
        {
            return "Running tests";
        }

        if (c.Contains("build") || c.Contains("tsc") || c.Contains("msbuild"))
        {
            return "Building";
        }

        if (c.StartsWith("git ", StringComparison.Ordinal))
        {
            return "Using git";
        }

        if (c.Contains("install") || c.StartsWith("npm i", StringComparison.Ordinal))
        {
            return "Installing packages";
        }

        var first = command.Trim().Split(' ', 2)[0];
        return $"Running {FileName(first) ?? first}";
    }

    private static string? CommandPreview(string? tool, JsonObject? input)
    {
        string? preview = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit" =>
                FileName(Str(input, "file_path") ?? Str(input, "notebook_path")),
            "WebFetch" => Str(input, "url"),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        var oneLine = preview.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxCommandPreview ? oneLine : oneLine[..(MaxCommandPreview - 1)] + "…";
    }

    private static string FailureText(string? error) => error switch
    {
        "rate_limit" => "Rate limited",
        "authentication_failed" or "auth" => "Sign-in problem",
        "overloaded" => "Service busy",
        null or "" => "Stopped with an error",
        _ => "Error: " + error.Replace('_', ' '),
    };

    private static void SetNeedsYou(AgentSession s, NeedsYouKind kind, string? tool, string? command, DateTimeOffset now)
    {
        s.State = AgentState.NeedsYou;
        s.NeedsYouKind = kind;
        s.PendingTool = tool;
        s.PendingCommand = command;
        s.SnoozedUntil = null;
        s.TurnStartedAt ??= now;
        s.Detail = kind == NeedsYouKind.Permission
            ? (tool is null ? "Needs permission" : $"Allow {tool}?")
            : "Waiting for your input";
    }

    private static void ClearPending(AgentSession s)
    {
        s.PendingTool = null;
        s.PendingCommand = null;
        s.SnoozedUntil = null;
    }

    private static string? ToolFromMessage(string? message)
    {
        // "Claude needs your permission to use Bash"
        const string marker = "to use ";
        var i = message?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return i < 0 ? null : message![(i + marker.Length)..].Trim().TrimEnd('.');
    }

    private static string ProjectName(string cwd)
    {
        var trimmed = cwd.TrimEnd('/', '\\');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length == 0 ? cwd : name;
    }

    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    private static bool Contains(string? text, string value) =>
        text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static string? Str(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
