using System.Net;
using System.Net.Http.Json;

namespace LyricsFloat.Core.Lyrics;

public sealed record LrcLibRecord(
    long Id,
    string? TrackName,
    string? ArtistName,
    string? AlbumName,
    double? Duration,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics);

/// <summary>LRCLIB answered with an error status; carries its Retry-After hint when it sent one.</summary>
public sealed class LrcLibException(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException($"LRCLIB responded with {(int)status} ({status})", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Client for https://lrclib.net, a free and open database of synced (LRC) lyrics.</summary>
public sealed class LrcLibClient(HttpClient http)
{
    /// <summary>Search results further than this from the playing track are treated as another recording.</summary>
    private const double DurationToleranceSeconds = 5;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves and gives them a higher rate limit for it.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyLyricsFloat/0.1 (personal desktop app)");
        return http;
    }

    public async Task<LrcLibRecord?> FindAsync(TrackInfo track, TimeSpan duration, CancellationToken ct)
    {
        LrcLibRecord? fallback = null;

        if (duration > TimeSpan.Zero)
        {
            var exact = await GetAsync(track, duration, ct);
            if (exact is { SyncedLyrics: not null } or { Instrumental: true }) return exact;
            fallback = exact;
        }

        var title = TrackNormalizer.CleanTitle(track.Title);
        var artist = TrackNormalizer.PrimaryArtist(track.Artist);
        string[] queries =
        [
            $"track_name={E(track.Title)}&artist_name={E(artist)}",
            $"track_name={E(title)}&artist_name={E(artist)}",
            $"q={E($"{title} {artist}")}",
        ];

        foreach (var query in queries.Distinct())
        {
            using var response = await SendAsync($"api/search?{query}", ct);
            List<LrcLibRecord> results = response is null ? [] : await response.Content.ReadFromJsonAsync<List<LrcLibRecord>>(ct) ?? [];
            var best = PickBest(results, duration);
            if (best is { SyncedLyrics: not null }) return best;
            fallback ??= best;
        }

        return fallback;
    }

    /// <summary>Prefers synced over plain lyrics, then the closest duration.</summary>
    public static LrcLibRecord? PickBest(IEnumerable<LrcLibRecord> results, TimeSpan duration)
    {
        var target = duration.TotalSeconds;
        double Distance(LrcLibRecord r) => target <= 0 || r.Duration is null ? 0 : Math.Abs(r.Duration.Value - target);

        return results
            .Where(r => Distance(r) <= DurationToleranceSeconds)
            .OrderByDescending(r => r.SyncedLyrics is not null)
            .ThenByDescending(r => r.PlainLyrics is not null)
            .ThenBy(Distance)
            .FirstOrDefault();
    }

    private async Task<LrcLibRecord?> GetAsync(TrackInfo track, TimeSpan duration, CancellationToken ct)
    {
        var url = $"api/get?track_name={E(track.Title)}&artist_name={E(track.Artist)}&duration={Math.Round(duration.TotalSeconds):0}";
        if (track.Album.Length > 0) url += $"&album_name={E(track.Album)}";

        using var response = await SendAsync(url, ct);
        return response is null ? null : await response.Content.ReadFromJsonAsync<LrcLibRecord>(ct);
    }

    /// <summary>Returns the response on success, null on 404, and throws <see cref="LrcLibException"/> otherwise.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            var retryAfter = response.Headers.RetryAfter;
            throw new LrcLibException(response.StatusCode, retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow);
        }
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
