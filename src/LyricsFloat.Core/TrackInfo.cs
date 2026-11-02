namespace LyricsFloat.Core;

public sealed record TrackInfo(string Title, string Artist, string Album)
{
    /// <summary>Stable identifier used for the lyrics cache and per-track sync offsets.</summary>
    public string Key => $"{Artist}|{Title}|{Album}".ToLowerInvariant();
}
