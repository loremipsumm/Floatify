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
public sealed class LrcLibClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Search results further than this from the playing track are treated as another recording.</summary>
    private const double DurationToleranceSeconds = 5;

    // LRCLIB sheds load with "503 ServerOverloaded" and a Retry-After of about a second; a short wait usually gets through.
    private const int AttemptsPerRequest = 3;
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves and gives them a higher rate limit for it.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Floatify/0.2 (+https://github.com/loremipsumm/Floatify)");
        return http;
    }

    /// <summary>
    /// Tries the cheapest, most exact lookups first and moves on when one fails, so a single busy
    /// endpoint can't sink the whole lookup. Throws only when LRCLIB could not be reached at all.
    /// </summary>
    public async Task<LrcLibRecord?> FindAsync(TrackInfo track, TimeSpan duration, CancellationToken ct)
    {
        var title = TrackNormalizer.CleanTitle(track.Title);
        var artist = TrackNormalizer.PrimaryArtist(track.Artist);
        var signature = SignatureQuery(track, duration);

        var lookups = new List<string>();
        if (duration > TimeSpan.Zero) lookups.Add($"api/get-cached?{signature}"); // database only, fast
        lookups.Add($"api/search?track_name={E(track.Title)}&artist_name={E(artist)}");
        lookups.Add($"api/search?track_name={E(title)}&artist_name={E(artist)}");
        if (duration > TimeSpan.Zero) lookups.Add($"api/get?{signature}");       // may consult outside sources
        lookups.Add($"api/search?q={E($"{title} {artist}")}");

        LrcLibRecord? fallback = null;
        Exception? lastError = null;
        var anyAnswered = false;

        foreach (var url in lookups.Distinct())
        {
            IReadOnlyList<LrcLibRecord> results;
            try
            {
                results = await FetchAsync(url, ct);
                anyAnswered = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && LookupRetry.IsTransient(ex))
            {
                lastError = ex;
                continue;
            }

            var best = PickBest(results, duration);
            if (best is { SyncedLyrics: not null } or { Instrumental: true }) return best;
            fallback ??= best;
        }

        // Some lookups answered cleanly, so "nothing found" is a real answer rather than an outage.
        if (fallback is null && !anyAnswered && lastError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(lastError);
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

    private static string SignatureQuery(TrackInfo track, TimeSpan duration)
    {
        var query = $"track_name={E(track.Title)}&artist_name={E(track.Artist)}&duration={Math.Round(duration.TotalSeconds):0}";
        return track.Album.Length > 0 ? query + $"&album_name={E(track.Album)}" : query;
    }

    /// <summary>Runs one lookup; "get" endpoints return a single record, "search" a list.</summary>
    private async Task<IReadOnlyList<LrcLibRecord>> FetchAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (response is null) return [];

        if (url.StartsWith("api/search", StringComparison.Ordinal))
            return await response.Content.ReadFromJsonAsync<List<LrcLibRecord>>(ct) ?? [];

        var record = await response.Content.ReadFromJsonAsync<LrcLibRecord>(ct);
        return record is null ? [] : [record];
    }

    /// <summary>Returns the response on success, null on 404, and throws <see cref="LrcLibException"/> otherwise.</summary>
    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;

                var header = response.Headers.RetryAfter;
                var retryAfter = header?.Delta ?? header?.Date - DateTimeOffset.UtcNow;
                var busy = response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
                var wait = retryAfter is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(1);

                if (!busy || attempt >= AttemptsPerRequest || wait > MaxInlineWait)
                    throw new LrcLibException(response.StatusCode, retryAfter);

                // Jitter keeps retries from many clients from landing on the same instant.
                await _delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 400)), ct);
            }
        }
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
