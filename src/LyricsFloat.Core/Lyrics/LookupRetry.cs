using System.Net;
using System.Text.Json;

namespace LyricsFloat.Core.Lyrics;

/// <summary>Decides whether a failed lyrics lookup is worth retrying, and how long to wait first.</summary>
public static class LookupRetry
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>Network trouble and server hiccups clear up on their own; anything else would fail the same way again.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true, // offline, DNS failure, connection refused or reset
        HttpRequestException { StatusCode: { } status } =>
            (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout,
        OperationCanceledException => true, // HttpClient timeout; callers filter out their own cancellation
        JsonException => true,              // e.g. a captive portal answering with an HTML login page
        IOException => true,
        _ => false,
    };

    /// <summary>How long to wait after the given number of consecutive failures, honouring the server's Retry-After.</summary>
    public static TimeSpan DelayAfter(int failures, Exception ex)
    {
        var backoff = Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)];
        var requested = (ex as LrcLibException)?.RetryAfter ?? TimeSpan.Zero;
        var delay = requested > backoff ? requested : backoff;
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
