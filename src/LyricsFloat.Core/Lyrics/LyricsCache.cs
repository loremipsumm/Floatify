using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsFloat.Core.Lyrics;

public sealed record LyricsEntry(string? SyncedLyrics, string? PlainLyrics, bool Instrumental, DateTimeOffset FetchedAt)
{
    [JsonIgnore]
    public bool IsEmpty => SyncedLyrics is null && PlainLyrics is null && !Instrumental;
}

/// <summary>
/// Keeps lookups on disk so each song is fetched once. Misses are retried after a day,
/// because LRCLIB is community-maintained and keeps gaining lyrics.
/// </summary>
public sealed class LyricsCache(string directory, Func<DateTimeOffset>? now = null)
{
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(1);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public LyricsEntry? Get(TrackInfo track)
    {
        try
        {
            var path = PathFor(track);
            if (!File.Exists(path)) return null;

            var entry = JsonSerializer.Deserialize<LyricsEntry>(File.ReadAllText(path));
            if (entry is null || (entry.IsEmpty && _now() - entry.FetchedAt > MissLifetime)) return null;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Put(TrackInfo track, LyricsEntry entry)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(PathFor(track), JsonSerializer.Serialize(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is best-effort; the lyrics still show for this session.
        }
    }

    private string PathFor(TrackInfo track) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(track.Key)))[..32] + ".json");
}
