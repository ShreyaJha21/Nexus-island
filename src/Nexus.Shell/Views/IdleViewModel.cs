using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Nexus.Modes;

namespace Nexus.Shell.Views;

/// <summary>
/// F7. Idle and launcher: the hover peek (time, date, next event or "All quiet") and the
/// launcher panel (clock, quick actions, the last finished activity).
/// </summary>
public sealed class IdleViewModel : ObservableObject
{
    private readonly DispatcherTimer _clock;
    private readonly Func<(string Text, DateTimeOffset At)?> _recent;
    private readonly Func<bool> _agentConnected;

    public IdleViewModel(
        Action startFocus,
        Action setupAgent,
        Func<(string Text, DateTimeOffset At)?> recent,
        Func<bool> agentConnected)
    {
        _recent = recent;
        _agentConnected = agentConnected;
        StartFocusCommand = new RelayCommand(startFocus);
        SetupAgentCommand = new RelayCommand(setupAgent);

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _clock.Tick += (_, _) => OnAllPropertiesChanged();
    }

    public ICommand StartFocusCommand { get; }
    public ICommand SetupAgentCommand { get; }

    public string FocusHint { get; set; } = "25 min";

    public string HideHint { get; set; } = "Ctrl+Alt+Space hides Nexus  ·  scroll on the island to switch modes";

    private static DateTime Now => DateTime.Now;
    private static bool Uses12Hour => CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t');

    /// <summary>"3:12 PM"</summary>
    public string PeekTime => Now.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"Mon 9 Nov"</summary>
    public string PeekDate => Now.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    /// <summary>Calendar arrives in v1, so the peek says "All quiet" for now.</summary>
    public string PeekStatus => "All quiet";

    /// <summary>"3:12" (the launcher shows the designator next to the date).</summary>
    public string ClockMain => Uses12Hour ? Now.ToString("h:mm", CultureInfo.CurrentCulture) : Now.ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"PM  ·  Monday, 9 November"</summary>
    public string ClockDetail
    {
        get
        {
            var date = Now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            return Uses12Hour ? $"{Now.ToString("tt", CultureInfo.CurrentCulture)}  ·  {date}" : date;
        }
    }

    public bool HasRecent => _recent() is not null;
    public string RecentText => _recent()?.Text ?? "";
    public string RecentAgo => _recent() is { } r ? Ago(r.At) : "";

    public bool AgentConnected => _agentConnected();

    public void OnOpened()
    {
        OnAllPropertiesChanged();
        _clock.Start();
    }

    public void OnClosed() => _clock.Stop();

    public void Refresh() => OnAllPropertiesChanged();

    private static string Ago(DateTimeOffset at)
    {
        var minutes = (int)(DateTimeOffset.UtcNow - at).TotalMinutes;
        return minutes switch
        {
            < 1 => "just now",
            < 60 => $"{minutes} min ago",
            _ => $"{minutes / 60} h ago",
        };
    }
}

/// <summary>Content of the "Why am I seeing this?" panel.</summary>
public sealed class WhyViewModel : ObservableObject
{
    public WhyViewModel(Action pause, Action close)
    {
        PauseCommand = new RelayCommand(pause);
        CloseCommand = new RelayCommand(close);
    }

    public ICommand PauseCommand { get; }
    public ICommand CloseCommand { get; }

    public string ModeName { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string PriorityText { get; private set; } = "";
    public bool CanPause { get; private set; }
    public string PauseText => $"Pause {ModeName.ToLowerInvariant()} for 1 hour";

    public void Show(string modeName, string reason, int priority, bool canPause)
    {
        ModeName = modeName;
        Reason = reason;
        PriorityText = $"Priority {priority} of 10  ·  lower numbers win the island";
        CanPause = canPause;
        OnAllPropertiesChanged();
    }
}
