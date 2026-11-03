using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class LrcLibClientTests
{
    private static readonly TrackInfo Track = new("Song - Remastered 2011", "First, Second", "Album");
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(200);
    private const string Synced = """{"id":7,"duration":200,"instrumental":false,"syncedLyrics":"[00:01.00] s"}""";

    [Fact]
    public void PickBest_prefers_synced_then_closest_duration()
    {
        LrcLibRecord[] results =
        [
            Record(1, duration: 200, synced: null, plain: "p"),
            Record(2, duration: 204, synced: "[00:01.00] s", plain: "p"),
            Record(3, duration: 201, synced: "[00:01.00] s", plain: "p"),
        ];

        Assert.Equal(3, LrcLibClient.PickBest(results, Duration)?.Id);
    }

    [Fact]
    public void PickBest_rejects_other_recordings()
    {
        LrcLibRecord[] results = [Record(1, duration: 260, synced: "[00:01.00] s", plain: null)];

        Assert.Null(LrcLibClient.PickBest(results, Duration));
    }

    [Fact]
    public async Task Starts_with_the_cached_exact_match()
    {
        var handler = new FakeHandler(_ => Json(Synced));

        var result = await CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None);

        Assert.Equal(7, result?.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/get-cached", request.AbsolutePath);
        Assert.Contains("duration=200", request.Query);
        Assert.Contains("album_name=Album", request.Query);
    }

    [Fact]
    public async Task Falls_back_to_search_with_cleaned_title()
    {
        var handler = new FakeHandler(uri => uri switch
        {
            _ when uri.AbsolutePath.StartsWith("/api/get") => NotFound(),
            _ when uri.Query.Contains("track_name=Song&") => Json($"[{Synced}]"),
            _ => Json("[]"),
        });

        var result = await CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None);

        Assert.Equal(7, result?.Id);
        Assert.Equal(3, handler.Requests.Count); // cached get, search with full title, search with cleaned title
        Assert.All(handler.Requests.Skip(1), r => Assert.Contains("artist_name=First", r.Query));
    }

    [Fact]
    public async Task Waits_out_a_short_busy_signal_and_retries()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var handler = new FakeHandler(_ => ++calls == 1 ? Busy(TimeSpan.FromSeconds(1)) : Json(Synced));

        var result = await CreateClient(handler, waits).FindAsync(Track, Duration, CancellationToken.None);

        Assert.Equal(7, result?.Id);
        Assert.Equal(2, handler.Requests.Count);
        var wait = Assert.Single(waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Keeps_looking_when_one_endpoint_stays_busy()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath == "/api/get-cached"
            ? Busy(TimeSpan.FromSeconds(1))
            : Json($"[{Synced}]"));

        var result = await CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None);

        Assert.Equal(7, result?.Id);
    }

    [Fact]
    public async Task Missing_song_is_not_found_even_if_some_endpoints_are_busy()
    {
        var handler = new FakeHandler(uri => uri switch
        {
            _ when uri.AbsolutePath == "/api/get-cached" => NotFound(),
            _ when uri.Query.Contains("track_name=") => Json("[]"),
            _ => Busy(TimeSpan.FromSeconds(1)), // the outside-source get and the free-text search
        });

        Assert.Null(await CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Returns_plain_lyrics_when_nothing_synced_exists()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath.StartsWith("/api/get")
            ? Json("""{"id":4,"duration":200,"instrumental":false,"plainLyrics":"p"}""")
            : Json("[]"));

        var result = await CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None);

        Assert.Equal(4, result?.Id);
    }

    [Fact]
    public async Task Reports_an_outage_with_the_servers_retry_after()
    {
        var handler = new FakeHandler(_ => Busy(TimeSpan.FromSeconds(42), HttpStatusCode.TooManyRequests));

        var error = await Assert.ThrowsAsync<LrcLibException>(
            () => CreateClient(handler).FindAsync(Track, Duration, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), error.RetryAfter);
    }

    private static LrcLibRecord Record(long id, double duration, string? synced, string? plain) =>
        new(id, "t", "a", "al", duration, false, plain, synced);

    private static LrcLibClient CreateClient(FakeHandler handler, List<TimeSpan>? waits = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.test/") },
            (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage Busy(TimeSpan retryAfter, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
