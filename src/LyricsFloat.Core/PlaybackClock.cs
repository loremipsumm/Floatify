namespace LyricsFloat.Core;

/// <summary>
/// Estimates the live playback position from sparse (position, timestamp) samples,
/// which is how Windows media sessions report Spotify's timeline.
/// </summary>
public sealed class PlaybackClock(Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime;
    private bool _playing;

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var position = _playing ? _anchorPosition + (_now() - _anchorTime) : _anchorPosition;
            if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    public void Update(TimeSpan position, DateTimeOffset sampledAt, bool playing, TimeSpan duration)
    {
        Duration = duration;

        var now = _now();
        // Apps that don't report a timestamp leave it at the WinRT epoch (1601).
        if (sampledAt.Year < 2000 || sampledAt > now) sampledAt = now;

        // Some players refresh the timestamp without refreshing the position while playing;
        // taking such a sample would make the clock jump backwards.
        if (playing && _playing && position == _anchorPosition && sampledAt > _anchorTime) return;

        _anchorPosition = position;
        _anchorTime = sampledAt;
        _playing = playing;
    }

    public void Reset()
    {
        _anchorPosition = TimeSpan.Zero;
        _anchorTime = _now();
        _playing = false;
        Duration = TimeSpan.Zero;
    }
}
