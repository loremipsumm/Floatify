using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class LyricsTimelineTests
{
    private static readonly LyricsTimeline Timeline = LyricsTimeline.FromSynced(
    [
        new LyricLine(TimeSpan.FromSeconds(2), "one"),
        new LyricLine(TimeSpan.FromSeconds(5), "two"),
        new LyricLine(TimeSpan.FromSeconds(9), "three"),
    ]);

    [Theory]
    [InlineData(0.0, -1)]
    [InlineData(1.99, -1)]
    [InlineData(2.0, 0)]
    [InlineData(4.9, 0)]
    [InlineData(5.0, 1)]
    [InlineData(9.0, 2)]
    [InlineData(300.0, 2)]
    public void Finds_line_playing_at_position(double seconds, int expected)
    {
        Assert.Equal(expected, Timeline.IndexAt(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Empty_timeline_has_no_current_line()
    {
        Assert.Equal(-1, LyricsTimeline.FromSynced([]).IndexAt(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Spreads_plain_lyrics_evenly_over_the_track()
    {
        var timeline = LyricsTimeline.FromPlain("a\n\nb\nc\nd\n", TimeSpan.FromSeconds(100));

        Assert.True(timeline.IsApproximate);
        Assert.Equal([0.0, 25, 50, 75], timeline.Lines.Select(l => l.Time.TotalSeconds));
    }

    [Fact]
    public void Prefers_synced_lyrics_from_an_entry()
    {
        var entry = new LyricsEntry("[00:01.00] synced", "plain", false, DateTimeOffset.Now);

        var timeline = LyricsTimeline.FromEntry(entry, TimeSpan.FromSeconds(60));

        Assert.NotNull(timeline);
        Assert.False(timeline.IsApproximate);
        Assert.Equal("synced", Assert.Single(timeline.Lines).Text);
    }

    [Fact]
    public void Falls_back_to_plain_lyrics_when_synced_cannot_be_parsed()
    {
        var entry = new LyricsEntry("no time tags here", "plain", false, DateTimeOffset.Now);

        var timeline = LyricsTimeline.FromEntry(entry, TimeSpan.FromSeconds(60));

        Assert.NotNull(timeline);
        Assert.True(timeline.IsApproximate);
    }

    [Fact]
    public void Entry_without_lyrics_gives_no_timeline()
    {
        Assert.Null(LyricsTimeline.FromEntry(new LyricsEntry(null, null, true, DateTimeOffset.Now), TimeSpan.FromSeconds(60)));
    }
}
