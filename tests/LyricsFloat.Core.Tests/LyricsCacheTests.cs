using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public sealed class LyricsCacheTests : IDisposable
{
    private static readonly TrackInfo Track = new("Song", "Artist", "Album");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lyrics-cache-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Round_trips_an_entry()
    {
        var cache = new LyricsCache(_dir, () => _now);
        var entry = new LyricsEntry("[00:01.00] s", "p", false, _now);

        cache.Put(Track, entry);

        Assert.Equal(entry, cache.Get(Track));
        Assert.Null(cache.Get(Track with { Title = "Other" }));
    }

    [Fact]
    public void Forgets_misses_after_a_day()
    {
        var cache = new LyricsCache(_dir, () => _now);
        cache.Put(Track, new LyricsEntry(null, null, false, _now));

        _now = _now.AddHours(23);
        Assert.NotNull(cache.Get(Track));

        _now = _now.AddHours(2);
        Assert.Null(cache.Get(Track));
    }

    [Fact]
    public void Keeps_found_lyrics_indefinitely()
    {
        var cache = new LyricsCache(_dir, () => _now);
        cache.Put(Track, new LyricsEntry(null, "p", false, _now));

        _now = _now.AddDays(365);

        Assert.NotNull(cache.Get(Track));
    }
}
