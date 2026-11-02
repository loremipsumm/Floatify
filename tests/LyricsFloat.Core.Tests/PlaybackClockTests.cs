namespace LyricsFloat.Core.Tests;

public class PlaybackClockTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(3);
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private PlaybackClock CreateClock() => new(() => _now);

    [Fact]
    public void Advances_from_the_last_sample_while_playing()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(10), _now.AddSeconds(-2), playing: true, Duration);

        _now = _now.AddSeconds(3);

        Assert.Equal(TimeSpan.FromSeconds(15), clock.Position);
    }

    [Fact]
    public void Stays_put_while_paused()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(10), _now.AddSeconds(-2), playing: false, Duration);

        _now = _now.AddMinutes(1);

        Assert.Equal(TimeSpan.FromSeconds(10), clock.Position);
    }

    [Fact]
    public void Follows_a_seek()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(10), _now, playing: true, Duration);
        _now = _now.AddSeconds(1);

        clock.Update(TimeSpan.FromSeconds(90), _now, playing: true, Duration);

        Assert.Equal(TimeSpan.FromSeconds(90), clock.Position);
    }

    [Fact]
    public void Ignores_refreshed_timestamp_with_stale_position()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(10), _now, playing: true, Duration);
        _now = _now.AddSeconds(5);

        clock.Update(TimeSpan.FromSeconds(10), _now, playing: true, Duration);

        Assert.Equal(TimeSpan.FromSeconds(15), clock.Position);
    }

    [Fact]
    public void Treats_missing_or_future_timestamps_as_now()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(10), new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), playing: true, Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Position);

        clock.Reset();
        clock.Update(TimeSpan.FromSeconds(20), _now.AddMinutes(5), playing: true, Duration);
        Assert.Equal(TimeSpan.FromSeconds(20), clock.Position);
    }

    [Fact]
    public void Never_runs_past_the_end_of_the_track()
    {
        var clock = CreateClock();
        clock.Update(TimeSpan.FromSeconds(170), _now, playing: true, Duration);

        _now = _now.AddMinutes(10);

        Assert.Equal(Duration, clock.Position);
    }
}
