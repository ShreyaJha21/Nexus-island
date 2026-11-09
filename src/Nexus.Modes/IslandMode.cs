using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Nexus.Core;

namespace Nexus.Modes;

/// <summary>How a mode asks the island to come forward for a moment.</summary>
public enum MomentKind
{
    /// <summary>Expand to the panel, hold for 3 s, collapse (track change, finished).</summary>
    Panel,
    /// <summary>Show the 56 px banner, hold, collapse (focus session done).</summary>
    Banner,
    /// <summary>Expand with the single orange edge pulse and hold until handled (agent needs you).</summary>
    Attention,
}

/// <summary>
/// One island mode. A mode owns its state, publishes an <see cref="Activity"/> for the priority
/// engine, and provides a compact view (the pill), an expanded view (the panel) and optionally
/// a bubble and a banner. Views are created once and bind to the mode as their DataContext.
/// </summary>
public abstract class IslandMode : ObservableObject
{
    private FrameworkElement? _compact;
    private FrameworkElement? _panel;
    private FrameworkElement? _bubble;
    private FrameworkElement? _banner;
    private bool _bubbleCreated;
    private bool _bannerCreated;

    public abstract string Id { get; }

    /// <summary>Name used in menus, e.g. "Pause Music for 1 hour".</summary>
    public abstract string Name { get; }

    public Activity? Activity { get; private set; }

    public event EventHandler? ActivityChanged;
    public event EventHandler<MomentKind>? MomentRequested;

    /// <summary>The mode handled something in its panel (e.g. "Remind me later") and wants the island to settle.</summary>
    public event EventHandler? CollapseRequested;

    public FrameworkElement CompactView => _compact ??= Attach(CreateCompactView());
    public FrameworkElement PanelView => _panel ??= Attach(CreatePanelView());

    public FrameworkElement? BubbleView
    {
        get
        {
            if (!_bubbleCreated)
            {
                _bubble = CreateBubbleView() is { } b ? Attach(b) : null;
                _bubbleCreated = true;
            }

            return _bubble;
        }
    }

    public FrameworkElement? BannerView
    {
        get
        {
            if (!_bannerCreated)
            {
                _banner = CreateBannerView() is { } b ? Attach(b) : null;
                _bannerCreated = true;
            }

            return _banner;
        }
    }

    /// <summary>When true the compact pill and panel get the 1.5 px orange "needs you" edge.</summary>
    public virtual bool HasAttentionEdge => false;

    /// <summary>Called by the shell when this mode's panel opens or closes, so modes can tick only while visible.</summary>
    public virtual void OnPanelOpened() { }

    public virtual void OnPanelClosed() { }

    /// <summary>Called when the shell needs the mode to drop its moment-specific view (e.g. banner dismissed).</summary>
    public virtual void OnMomentEnded(MomentKind kind) { }

    protected abstract FrameworkElement CreateCompactView();

    protected abstract FrameworkElement CreatePanelView();

    protected virtual FrameworkElement? CreateBubbleView() => null;

    protected virtual FrameworkElement? CreateBannerView() => null;

    protected void SetActivity(Activity? activity)
    {
        if (Equals(activity, Activity))
        {
            return;
        }

        Activity = activity;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected void RequestMoment(MomentKind kind) => MomentRequested?.Invoke(this, kind);

    protected void RequestCollapse() => CollapseRequested?.Invoke(this, EventArgs.Empty);

    private FrameworkElement Attach(FrameworkElement view)
    {
        view.DataContext = this;
        return view;
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Refreshes every binding on this object.</summary>
    protected void OnAllPropertiesChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}
