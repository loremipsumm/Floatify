using System.Net;
using System.Text.Json;
using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class LookupRetryTests
{
    public static TheoryData<Exception, bool> Errors => new()
    {
        { new HttpRequestException("offline"), true },
        { new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), true },
        { new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests), true },
        { new HttpRequestException("bad request", null, HttpStatusCode.BadRequest), false },
        { new TaskCanceledException("timeout"), true },
        { new JsonException("html instead of json"), true },
        { new IOException("connection reset"), true },
        { new InvalidOperationException("bug"), false },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Retries_only_errors_that_can_clear_up(Exception error, bool expected)
    {
        Assert.Equal(expected, LookupRetry.IsTransient(error));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 20)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void Backs_off_gradually(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LookupRetry.DelayAfter(failures, new HttpRequestException()));
    }

    [Fact]
    public void Honours_a_longer_retry_after_from_the_server()
    {
        var error = new LrcLibException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), LookupRetry.DelayAfter(1, error));
    }

    [Fact]
    public void Caps_the_wait_at_five_minutes()
    {
        var error = new LrcLibException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromMinutes(5), LookupRetry.DelayAfter(1, error));
    }
}
