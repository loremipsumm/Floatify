// Diagnostic tool: prints what Windows Media Control (SMTC) reports for each media
// session, so we can check whether Spotify publishes an accurate timeline position.
using Windows.Media.Control;

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 10;

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

Console.WriteLine("Sessions:");
foreach (var session in manager.GetSessions())
    Console.WriteLine($"  - {session.SourceAppUserModelId}");

var spotify = manager.GetSessions()
    .FirstOrDefault(x => x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

if (spotify is null)
{
    Console.WriteLine("Spotify session not found.");
    return 1;
}

var props = await spotify.TryGetMediaPropertiesAsync();
Console.WriteLine($"Track : {props.Title}");
Console.WriteLine($"Artist: {props.Artist}");
Console.WriteLine($"Album : {props.AlbumTitle}");
var controls = spotify.GetPlaybackInfo().Controls;
Console.WriteLine($"Controls: playPause={controls.IsPlayPauseToggleEnabled} next={controls.IsNextEnabled} " +
                  $"previous={controls.IsPreviousEnabled} seek={controls.IsPlaybackPositionEnabled}");
Console.WriteLine();
Console.WriteLine("local time    | status  | position  | lastUpdated   | extrapolated | end");

var end = DateTime.Now.AddSeconds(seconds);
while (DateTime.Now < end)
{
    var info = spotify.GetPlaybackInfo();
    var t = spotify.GetTimelineProperties();
    var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    var extrapolated = t.Position + (playing ? DateTimeOffset.Now - t.LastUpdatedTime : TimeSpan.Zero);

    Console.WriteLine(
        $"{DateTime.Now:HH:mm:ss.fff}  | {info.PlaybackStatus,-7} | {t.Position:mm\\:ss\\.ff}  | " +
        $"{t.LastUpdatedTime.ToLocalTime():HH:mm:ss.fff}  | {extrapolated:mm\\:ss\\.ff}     | {t.EndTime:mm\\:ss}");

    await Task.Delay(500);
}

return 0;
