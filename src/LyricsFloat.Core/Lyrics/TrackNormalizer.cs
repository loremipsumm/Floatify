using System.Text.RegularExpressions;

namespace LyricsFloat.Core.Lyrics;

/// <summary>Strips version decorations from Spotify titles so fallback searches can match.</summary>
public static partial class TrackNormalizer
{
    // "Song - Remastered 2011", "Song - Live at X", "Song - Radio Edit"
    [GeneratedRegex(@"\s+-\s+.*$")]
    private static partial Regex DashSuffix();

    // "(feat. X)", "[with Y]", "(2009 Remaster)", "(Live)"
    [GeneratedRegex(@"\s*[\(\[](?:feat\.?|ft\.?|with|remaster|\d{4}\s+remaster|live|radio edit|mono|stereo|deluxe|bonus)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Decoration();

    private static readonly string[] ArtistSeparators = [", ", " & ", " feat. ", " ft. ", " x "];

    public static string CleanTitle(string title) =>
        Decoration().Replace(DashSuffix().Replace(title, ""), "").Trim();

    public static string PrimaryArtist(string artist) =>
        artist.Split(ArtistSeparators, 2, StringSplitOptions.None)[0].Trim();
}
