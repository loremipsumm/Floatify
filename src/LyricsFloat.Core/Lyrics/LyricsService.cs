namespace LyricsFloat.Core.Lyrics;

/// <summary>Looks lyrics up in the local cache first, then on LRCLIB.</summary>
public sealed class LyricsService(LrcLibClient client, LyricsCache cache)
{
    public async Task<LyricsEntry> GetAsync(TrackInfo track, TimeSpan duration, CancellationToken ct)
    {
        if (cache.Get(track) is { } cached) return cached;

        var record = await client.FindAsync(track, duration, ct);
        var entry = new LyricsEntry(
            NullIfBlank(record?.SyncedLyrics),
            NullIfBlank(record?.PlainLyrics),
            record?.Instrumental ?? false,
            DateTimeOffset.Now);
        cache.Put(track, entry);
        return entry;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
