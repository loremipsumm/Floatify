using System.Net;
using System.Text;
using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class LrcLibClientTests
{
    private static readonly TrackInfo Track = new("Song - Remastered 2011", "First, Second", "Album");

    [Fact]
    public void PickBest_prefers_synced_then_closest_duration()
    {
        LrcLibRecord[] results =
        [
            Record(1, duration: 200, synced: null, plain: "p"),
            Record(2, duration: 204, synced: "[00:01.00] s", plain: "p"),
            Record(3, duration: 201, synced: "[00:01.00] s", plain: "p"),
        ];

        Assert.Equal(3, LrcLibClient.PickBest(results, TimeSpan.FromSeconds(200))?.Id);
    }

    [Fact]
    public void PickBest_rejects_other_recordings()
    {
        LrcLibRecord[] results = [Record(1, duration: 260, synced: "[00:01.00] s", plain: null)];

        Assert.Null(LrcLibClient.PickBest(results, TimeSpan.FromSeconds(200)));
    }

    [Fact]
    public async Task Uses_exact_match_when_it_has_synced_lyrics()
    {
        var handler = new FakeHandler(_ => Json("""{"id":7,"duration":200,"instrumental":false,"syncedLyrics":"[00:01.00] s"}"""));

        var result = await CreateClient(handler).FindAsync(Track, TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.Equal(7, result?.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/get", request.AbsolutePath);
        Assert.Contains("duration=200", request.Query);
        Assert.Contains("album_name=Album", request.Query);
    }

    [Fact]
    public async Task Falls_back_to_search_with_cleaned_title()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath switch
        {
            "/api/get" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ when uri.Query.Contains("track_name=Song&") =>
                Json("""[{"id":9,"duration":201,"instrumental":false,"syncedLyrics":"[00:01.00] s"}]"""),
            _ => Json("[]"),
        });

        var result = await CreateClient(handler).FindAsync(Track, TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.Equal(9, result?.Id);
        Assert.Equal(3, handler.Requests.Count); // exact get, search with full title, search with cleaned title
        Assert.All(handler.Requests.Skip(1), r => Assert.Contains("artist_name=First", r.Query));
    }

    [Fact]
    public async Task Returns_plain_lyrics_when_nothing_synced_exists()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath == "/api/get"
            ? Json("""{"id":4,"duration":200,"instrumental":false,"plainLyrics":"p"}""")
            : Json("[]"));

        var result = await CreateClient(handler).FindAsync(Track, TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.Equal(4, result?.Id);
    }

    [Fact]
    public async Task Reports_server_errors_with_their_retry_after()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var error = await Assert.ThrowsAsync<LrcLibException>(
            () => CreateClient(handler).FindAsync(Track, TimeSpan.FromSeconds(200), CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), error.RetryAfter);
    }

    private static LrcLibRecord Record(long id, double duration, string? synced, string? plain) =>
        new(id, "t", "a", "al", duration, false, plain, synced);

    private static LrcLibClient CreateClient(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.test/") });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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
