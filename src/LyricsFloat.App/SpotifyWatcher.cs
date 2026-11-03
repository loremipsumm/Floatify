using System.Runtime.InteropServices;
using LyricsFloat.Core;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LyricsFloat.App;

public sealed record PlaybackSnapshot(
    bool SpotifyFound,
    TrackInfo? Track,
    bool IsPlaying,
    TimeSpan Position,
    DateTimeOffset SampledAt,
    TimeSpan Duration,
    bool CanSeek = false,
    bool CanSkip = false,
    IRandomAccessStreamReference? Artwork = null,
    bool CanShuffle = false,
    bool CanRepeat = false,
    bool? ShuffleActive = null,
    MediaPlaybackAutoRepeatMode? RepeatMode = null)
{
    public static readonly PlaybackSnapshot NoSpotify = new(false, null, false, TimeSpan.Zero, default, TimeSpan.Zero);
}

/// <summary>
/// Follows the Spotify desktop app through Windows' media session API (SMTC), the same source
/// the volume flyout uses, so it needs no login or API key.
/// </summary>
public sealed class SpotifyWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Timer? _poll;
    private volatile bool _sessionsChanged = true;
    private int _refreshing;
    private volatile bool _refreshAgain;

    /// <summary>Raised on a background thread each time playback state is read.</summary>
    public event Action<PlaybackSnapshot>? Updated;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        // Events cover most changes; the poll catches anything SMTC fails to signal.
        _poll = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _poll?.Dispose();
        if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
        Attach(null);
    }

    public Task TogglePlayPauseAsync() => SendAsync(s => s.TryTogglePlayPauseAsync());

    public Task NextAsync() => SendAsync(s => s.TrySkipNextAsync());

    public Task PreviousAsync() => SendAsync(s => s.TrySkipPreviousAsync());

    public Task SeekAsync(TimeSpan position) => SendAsync(s => s.TryChangePlaybackPositionAsync(position.Ticks));

    public Task SetShuffleAsync(bool on) => SendAsync(s => s.TryChangeShuffleActiveAsync(on));

    public Task SetRepeatAsync(MediaPlaybackAutoRepeatMode mode) => SendAsync(s => s.TryChangeAutoRepeatModeAsync(mode));

    private async Task SendAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        if (_session is not { } session) return;
        try
        {
            await command(session);
        }
        catch (COMException ex)
        {
            Log.Error("Media command failed", ex);
        }
        _ = RefreshAsync();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _sessionsChanged = true;
        _ = RefreshAsync();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // Events and the poll can fire together; run one refresh at a time and repeat if more arrived.
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshAgain = true;
            return;
        }

        try
        {
            do
            {
                _refreshAgain = false;
                await RefreshCoreAsync();
            } while (_refreshAgain);
        }
        catch (Exception ex)
        {
            Log.Error("Reading media session failed", ex);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (_manager is null) return;

        if (_sessionsChanged || _session is null)
        {
            _sessionsChanged = false;
            Attach(_manager.GetSessions().FirstOrDefault(IsSpotify));
        }

        var session = _session;
        if (session is null)
        {
            Updated?.Invoke(PlaybackSnapshot.NoSpotify);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            // Mid track change Windows can briefly hand back nothing; skip this read, the next poll retries.
            if (playback is null || timeline is null) return;

            Updated?.Invoke(new PlaybackSnapshot(
                SpotifyFound: true,
                Track: ToTrack(props),
                IsPlaying: playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position: timeline.Position,
                SampledAt: timeline.LastUpdatedTime,
                Duration: timeline.EndTime - timeline.StartTime,
                CanSeek: playback.Controls.IsPlaybackPositionEnabled,
                CanSkip: playback.Controls.IsNextEnabled,
                Artwork: props?.Thumbnail,
                CanShuffle: playback.Controls.IsShuffleEnabled,
                CanRepeat: playback.Controls.IsRepeatEnabled,
                ShuffleActive: playback.IsShuffleActive,
                RepeatMode: playback.AutoRepeatMode));
        }
        catch (COMException)
        {
            // Spotify closed between reads; find the session again next time.
            Attach(null);
            Updated?.Invoke(PlaybackSnapshot.NoSpotify);
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }

        _session = session;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnSessionChanged;
        }
    }

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;

    private static TrackInfo? ToTrack(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        if (props is null || string.IsNullOrWhiteSpace(props.Title)) return null;

        // Ads on Spotify Free come through as a "track" with no artist, or by Spotify itself.
        if (string.IsNullOrWhiteSpace(props.Artist) || props.Artist.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
            return null;

        return new TrackInfo(props.Title.Trim(), props.Artist.Trim(), props.AlbumTitle?.Trim() ?? "");
    }
}
