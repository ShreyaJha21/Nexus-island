using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Agent;

namespace Nexus.Modes.Agent;

/// <summary>
/// F3. Agent status for Claude Code (and any agent that posts the same events).
/// Working shows the current tool and target; Needs you always expands, even during focus;
/// Done fades after 10 s; Failed stays until dismissed; several sessions get a count.
/// </summary>
public sealed class AgentMode : IslandMode
{
    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(5);

    private readonly AgentTracker _tracker = new();
    private readonly Action<string?> _openTerminal;
    private readonly Action _openSetup;
    private readonly DispatcherTimer _housekeeping;
    private AgentSession? _primary;
    private bool _attentionPending;

    /// <param name="openTerminal">Brings the agent's terminal or editor forward; argument is the session's working folder.</param>
    /// <param name="openSetup">Opens the hook setup screen.</param>
    public AgentMode(Action<string?> openTerminal, Action openSetup)
    {
        _openTerminal = openTerminal;
        _openSetup = openSetup;
        // Raise the moment only after this mode's state is refreshed, so the panel opens with current data.
        _tracker.NeedsYouRaised += _ => _attentionPending = true;

        _housekeeping = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _housekeeping.Tick += (_, _) => Housekeeping();

        OpenTerminalCommand = new RelayCommand(p =>
        {
            var cwd = (p as AgentSessionRow)?.Cwd ?? _primary?.Cwd;
            _openTerminal(cwd);
        });
        RemindLaterCommand = new RelayCommand(() =>
        {
            if (_primary is not null)
            {
                _tracker.Snooze(_primary.Id, SnoozeFor, DateTimeOffset.UtcNow);
                Refresh();
                RequestCollapse();
            }
        });
        DismissCommand = new RelayCommand(p =>
        {
            var id = (p as AgentSessionRow)?.Id ?? _primary?.Id;
            if (id is not null)
            {
                _tracker.Dismiss(id);
                Refresh();
            }
        });
        SetupCommand = new RelayCommand(() => _openSetup());
    }

    public override string Id => "agent";
    public override string Name => "Agent";

    public ICommand OpenTerminalCommand { get; }
    public ICommand RemindLaterCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand SetupCommand { get; }

    public ObservableCollection<AgentSessionRow> Rows { get; } = [];

    /// <summary>Last time any event arrived; proves the hooks are wired up.</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    public int Count => Rows.Count;
    public bool IsMulti => Count > 1;
    public bool IsSingle => Count == 1;

    public AgentState PrimaryState => _primary?.State ?? AgentState.Idle;
    public bool IsWorking => IsSingle && PrimaryState == AgentState.Working;
    public bool IsNeedsYou => PrimaryState == AgentState.NeedsYou;
    public bool IsPermission => IsNeedsYou && _primary?.NeedsYouKind == NeedsYouKind.Permission;
    public bool IsWaitingForInput => IsNeedsYou && _primary?.NeedsYouKind == NeedsYouKind.Input;

    /// <summary>The panel shows the permission card when the top session needs the user.</summary>
    public bool ShowNeedsYouPanel => IsNeedsYou;
    public bool ShowSessionsPanel => !IsNeedsYou;

    public override bool HasAttentionEdge => IsNeedsYou && _primary?.SnoozedUntil is null;

    // ---- Compact pill ----

    public string CompactIcon => IsMulti ? (IsNeedsYou ? "attention" : "bot") : IconFor(PrimaryState);
    public Brush CompactIconBrush => IsMulti && !IsNeedsYou ? Res("Brush.Accent") : BrushFor(PrimaryState);

    public string CompactTitle
    {
        get
        {
            var agent = _primary?.Agent ?? "Claude";
            if (IsMulti)
            {
                return $"{agent} × {Count}";
            }

            return PrimaryState switch
            {
                AgentState.NeedsYou => $"{agent} needs you",
                AgentState.Done => _primary!.Detail,
                AgentState.Failed => $"{agent} stopped",
                _ => agent,
            };
        }
    }

    public string CompactCaption => PrimaryState switch
    {
        AgentState.Done => _primary!.Project,
        AgentState.Failed => $"{_primary!.Detail}  ·  view",
        _ => _primary?.Detail ?? "",
    };

    public int NeedsYouCount => Rows.Count(r => r.State == AgentState.NeedsYou);
    public string NeedsYouChip => $"{NeedsYouCount} needs you";
    public bool ShowNeedsYouChip => IsMulti && NeedsYouCount > 0;
    public bool ShowCompactCaption => !IsMulti && !string.IsNullOrEmpty(CompactCaption);

    // ---- Needs-you panel ----

    public string NeedsYouTitle => IsPermission
        ? $"{_primary?.Agent ?? "Claude"} needs your permission"
        : $"{_primary?.Agent ?? "Claude"} is waiting for you";

    public string NeedsYouContext => IsPermission
        ? $"{_primary?.Project}  ·  wants to {Want(_primary?.PendingTool)}"
        : $"{_primary?.Project}  ·  finished and waiting for your next message";

    public string? PendingCommand => _primary?.PendingCommand;
    public bool HasPendingCommand => IsPermission && !string.IsNullOrEmpty(PendingCommand);
    public string NeedsYouAge => Age(_primary?.TurnStartedAt, suffix: false);
    public string OtherSessionsText => Count > 1 ? $"{Count - 1} other session{(Count == 2 ? "" : "s")} running" : "";

    // ---- Sessions panel ----

    public string PanelHeader => $"{_primary?.Agent ?? "Claude"} Code";
    public string PanelCount => Count == 1 ? "1 session" : $"{Count} sessions";
    public bool HasFailed => Rows.Any(r => r.State == AgentState.Failed);

    // ---- Launcher ----

    public string? LastFinishedText => _tracker.LastFinished is { } s
        ? $"{s.Agent} finished{(s.TurnDuration is { } d ? $" in {AgentTracker.FormatDuration(d)}" : "")}"
        : null;

    public DateTimeOffset? LastFinishedAt => _tracker.LastFinished?.FinishedAt;

    /// <summary>Call on the UI thread for each event received from the local endpoint.</summary>
    public void Apply(AgentEvent e)
    {
        LastEventAt = DateTimeOffset.UtcNow;
        _tracker.Apply(e, DateTimeOffset.UtcNow);
        Refresh();
    }

    public override void OnPanelOpened() => Refresh();

    protected override FrameworkElement CreateCompactView() => new AgentCompactView();
    protected override FrameworkElement CreatePanelView() => new AgentPanelView();
    protected override FrameworkElement? CreateBubbleView() => new AgentBubbleView();

    private void Housekeeping()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = _tracker.Expire(now) | _tracker.WakeSnoozed(now);
        if (changed)
        {
            Refresh();
        }
        else
        {
            // Keep the "2 min" / "now" labels honest while something is on screen.
            foreach (var row in Rows)
            {
                row.Update(now);
            }

            OnPropertyChanged(nameof(NeedsYouAge));
        }
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var visible = _tracker.VisibleSessions.ToList();
        _primary = visible.FirstOrDefault();

        Rows.Clear();
        foreach (var s in visible)
        {
            Rows.Add(new AgentSessionRow(s, now));
        }

        _housekeeping.IsEnabled = visible.Count > 0;
        OnAllPropertiesChanged();
        SetActivity(BuildActivity());

        if (_attentionPending)
        {
            _attentionPending = false;
            RequestMoment(MomentKind.Attention);
        }
    }

    private Activity? BuildActivity()
    {
        if (_primary is null)
        {
            return null;
        }

        var urgent = _primary.State == AgentState.NeedsYou && _primary.SnoozedUntil is null;
        var priority = urgent ? Priority.AgentNeedsYou : Priority.Status;
        var since = Activity is { } current && current.Priority == priority ? current.Since : DateTimeOffset.UtcNow;

        var reason = _primary.State switch
        {
            AgentState.NeedsYou when urgent =>
                $"{_primary.Agent} Code in {_primary.Project} is waiting for {(IsPermission ? "permission" : "your input")}. " +
                "\"Agent needs you\" always shows first, even during focus.",
            AgentState.NeedsYou => $"{_primary.Agent} Code in {_primary.Project} is waiting for you. You chose \"Remind me later\".",
            AgentState.Failed => $"{_primary.Agent} Code in {_primary.Project} stopped with an error. It stays until you open or dismiss it.",
            AgentState.Done => $"{_primary.Agent} Code in {_primary.Project} just finished. This fades after 10 seconds.",
            _ => $"{_primary.Agent} Code is working in {_primary.Project}.",
        };

        return new Activity(Id, priority, since) { BreaksFocus = urgent, Reason = reason };
    }

    internal static string IconFor(AgentState state) => state switch
    {
        AgentState.NeedsYou => "attention",
        AgentState.Done => "check",
        AgentState.Failed => "error",
        _ => "bot",
    };

    internal static Brush BrushFor(AgentState state) => state switch
    {
        AgentState.NeedsYou => Res("Brush.Attention"),
        AgentState.Done => Res("Brush.Success"),
        AgentState.Failed => Res("Brush.Critical"),
        _ => Res("Brush.Accent"),
    };

    internal static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    internal static string Age(DateTimeOffset? since, bool suffix)
    {
        if (since is null)
        {
            return "";
        }

        var minutes = (int)(DateTimeOffset.UtcNow - since.Value).TotalMinutes;
        return minutes < 1 ? "now" : suffix ? $"{minutes} min ago" : $"{minutes} min";
    }

    private static string Want(string? tool) => tool switch
    {
        "Bash" or "PowerShell" => "run a command",
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "edit a file",
        "WebFetch" or "WebSearch" => "use the web",
        null or "" => "use a tool",
        _ => $"use {tool}",
    };
}

/// <summary>One row in the sessions panel (Figma: Panel · Agent sessions, 18:254).</summary>
public sealed class AgentSessionRow : ObservableObject
{
    private readonly AgentSession _session;

    public AgentSessionRow(AgentSession session, DateTimeOffset now)
    {
        _session = session;
        Update(now);
    }

    public string Id => _session.Id;
    public string? Cwd => _session.Cwd;
    public AgentState State => _session.State;
    public string Project => _session.Project;
    public string Detail => _session.State == AgentState.NeedsYou && _session.PendingCommand is { } cmd
        ? $"Needs permission: {cmd}"
        : _session.Detail;
    public string Icon => AgentMode.IconFor(_session.State);
    public Brush IconBrush => AgentMode.BrushFor(_session.State);
    public Brush DetailBrush => _session.State == AgentState.NeedsYou
        ? AgentMode.Res("Brush.Attention")
        : AgentMode.Res("Brush.TextSecondary");
    public bool CanDismiss => _session.State is AgentState.Failed or AgentState.Done;
    public string Time { get; private set; } = "";

    public void Update(DateTimeOffset now)
    {
        Time = _session.State switch
        {
            AgentState.Done or AgentState.Failed => AgentMode.Age(_session.FinishedAt, suffix: true),
            AgentState.NeedsYou => AgentMode.Age(_session.UpdatedAt, suffix: false),
            _ => AgentMode.Age(_session.TurnStartedAt, suffix: false),
        };
        OnPropertyChanged(nameof(Time));
    }
}
