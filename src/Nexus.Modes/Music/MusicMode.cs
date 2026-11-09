using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Settings;
using Windows.Media.Control;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Nexus.Modes.Music;

/// <summary>
/// F4. Music. Works with any app that reports to Windows media controls (Spotify, browsers,
/// Apple Music, VLC). Event-driven: no polling, except a 1 s progress tick while the panel is open.
/// </summary>
public sealed class MusicMode : IslandMode
{
    /// <summary>After pausing, the pill stays this long so you can resume from it, then Nexus goes quiet.</summary>
    private static readonly TimeSpan PausedGrace = TimeSpan.FromSeconds(60);

    private readonly NexusSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _progressTick;
    private readonly DispatcherTimer _graceTimer;
    private MediaSessionManager? _manager;
    private MediaSession? _session;
    private string _trackKey = "";
    private bool _userSkipped;
    private DateTimeOffset _pausedAt;

    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _positionUpdatedAt;

    public MusicMode(NexusSettings settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _progressTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _progressTick.Tick += (_, _) => RaiseProgress();
        _graceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PausedGrace };
        _graceTimer.Tick += (_, _) =>
        {
            _graceTimer.Stop();
            UpdateActivity();
        };

        PreviousCommand = new RelayCommand(async () =>
        {
            _userSkipped = true;
            if (_session is not null) await _session.TrySkipPreviousAsync();
        });
        PlayPauseCommand = new RelayCommand(async () =>
        {
            if (_session is not null) await _session.TryTogglePlayPauseAsync();
        });
        NextCommand = new RelayCommand(async () =>
        {
            _userSkipped = true;
            if (_session is not null) await _session.TrySkipNextAsync();
        });
    }

    public override string Id => "music";
    public override string Name => "Music";

    public ICommand PreviousCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string SourceText { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public ImageSource? AlbumArt { get; private set; }
    public bool HasArt => AlbumArt is not null;
    public bool IsPlaying { get; private set; }
    public bool HasArtist => !string.IsNullOrWhiteSpace(Artist);

    public string PlayPauseIcon => IsPlaying ? "pause" : "play";
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying)
            {
                p += DateTimeOffset.UtcNow - _positionUpdatedAt;
            }

            return _duration > TimeSpan.Zero && p > _duration ? _duration : p;
        }
    }

    public double Progress => _duration > TimeSpan.Zero ? Math.Clamp(Position / _duration, 0, 1) : 0;
    public string PositionText => Format(Position);
    public string DurationText => Format(_duration);
    public bool HasTimeline => _duration > TimeSpan.Zero;

    public async Task StartAsync()
    {
        try
        {
            _manager = await MediaSessionManager.RequestAsync();
        }
        catch (Exception)
        {
            // Media controls unavailable (e.g. Windows N without the Media Feature Pack). Music stays off.
            return;
        }

        _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(AttachCurrentSession);
        AttachCurrentSession();
    }

    public override void OnPanelOpened()
    {
        RaiseProgress();
        _progressTick.Start();
    }

    public override void OnPanelClosed() => _progressTick.Stop();

    protected override FrameworkElement CreateCompactView() => new MusicCompactView();
    protected override FrameworkElement CreatePanelView() => new MusicPanelView();
    protected override FrameworkElement? CreateBubbleView() => new MusicBubbleView();

    private void AttachCurrentSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();
        if (_session is null)
        {
            Title = Artist = SourceText = AppId = "";
            AlbumArt = null;
            IsPlaying = false;
            _trackKey = "";
            OnAllPropertiesChanged();
            UpdateActivity();
            return;
        }

        AppId = _session.SourceAppUserModelId ?? "";
        SourceText = $"Playing in {FriendlyAppName(AppId)}";
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelineChanged;

        _ = ReadMediaPropertiesAsync(_session, initial: true);
        ReadPlaybackInfo(_session);
        ReadTimeline(_session);
    }

    private void OnMediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => ReadMediaPropertiesAsync(sender, initial: false));

    private void OnPlaybackInfoChanged(MediaSession sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => ReadPlaybackInfo(sender));

    private void OnTimelineChanged(MediaSession sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => ReadTimeline(sender));

    private async Task ReadMediaPropertiesAsync(MediaSession session, bool initial)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception)
        {
            return;
        }

        if (!ReferenceEquals(session, _session) || props is null)
        {
            return;
        }

        var key = $"{props.Title}\u0001{props.Artist}";
        var changed = key != _trackKey;
        _trackKey = key;

        Title = props.Title ?? "";
        Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
        AlbumArt = changed || AlbumArt is null ? await LoadArtAsync(props) : AlbumArt;
        OnAllPropertiesChanged();
        UpdateActivity();

        // A track change expands briefly, unless the user just pressed next/previous here,
        // the app is excluded, or this is the first read after attaching.
        if (changed && !initial && IsPlaying && !string.IsNullOrWhiteSpace(Title) && !_userSkipped && !IsExcluded(AppId))
        {
            RequestMoment(MomentKind.Panel);
        }

        _userSkipped = false;
    }

    private void ReadPlaybackInfo(MediaSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        var info = session.GetPlaybackInfo();
        var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing != IsPlaying)
        {
            // Freeze the extrapolated position at the moment playback state flips.
            _position = Position;
            _positionUpdatedAt = DateTimeOffset.UtcNow;
            IsPlaying = playing;
            if (!playing)
            {
                _pausedAt = DateTimeOffset.UtcNow;
                _graceTimer.Stop();
                _graceTimer.Start();
            }
        }

        OnAllPropertiesChanged();
        UpdateActivity();
    }

    private void ReadTimeline(MediaSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        var t = session.GetTimelineProperties();
        if (t is null)
        {
            return;
        }

        _duration = t.EndTime - t.StartTime;
        _position = t.Position - t.StartTime;
        _positionUpdatedAt = t.LastUpdatedTime == default ? DateTimeOffset.UtcNow : t.LastUpdatedTime;
        if (_positionUpdatedAt > DateTimeOffset.UtcNow)
        {
            _positionUpdatedAt = DateTimeOffset.UtcNow;
        }

        RaiseProgress();
    }

    private void UpdateActivity()
    {
        var hasTrack = _session is not null && !string.IsNullOrWhiteSpace(Title);
        var recentlyPaused = !IsPlaying && DateTimeOffset.UtcNow - _pausedAt < PausedGrace;
        if (!hasTrack || (!IsPlaying && !recentlyPaused))
        {
            SetActivity(null);
            return;
        }

        var since = Activity?.Since ?? DateTimeOffset.UtcNow;
        SetActivity(new Activity(Id, Priority.Music, since)
        {
            Reason = $"Music is playing in {FriendlyAppName(AppId)}. Music has the lowest priority, so anything else takes the island first.",
        });
    }

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasTimeline));
    }

    private bool IsExcluded(string appId) =>
        _settings.Music.ExcludedApps.Any(x => !string.IsNullOrWhiteSpace(x) && appId.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static async Task<ImageSource?> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null)
        {
            return null;
        }

        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 112; // 56 px at 200% scale
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FriendlyAppName(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic") || id.Contains("apple music")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("media.player")) return "Media Player";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("chrome") || id.Contains("msedge") || id.Contains("firefox") || id.Contains("brave") ||
            id.Contains("opera") || id.Contains("vivaldi") || id == "308046b0af4a39cb") return "your browser";

        var name = appId.Split('!')[^1];
        name = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(name) ? "an app" : name;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
