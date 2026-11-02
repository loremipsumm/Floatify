using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class TrackNormalizerTests
{
    [Theory]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song - Live at Somewhere", "Song")]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song [with Someone]", "Song")]
    [InlineData("Song (2009 Remaster)", "Song")]
    [InlineData("Song (Live)", "Song")]
    [InlineData("Anti-Hyphen Title", "Anti-Hyphen Title")]
    [InlineData("Song (Interlude)", "Song (Interlude)")]
    public void Cleans_titles(string title, string expected)
    {
        Assert.Equal(expected, TrackNormalizer.CleanTitle(title));
    }

    [Theory]
    [InlineData("Solo Artist", "Solo Artist")]
    [InlineData("First, Second", "First")]
    [InlineData("First & Second", "First")]
    [InlineData("First feat. Second", "First")]
    public void Picks_primary_artist(string artist, string expected)
    {
        Assert.Equal(expected, TrackNormalizer.PrimaryArtist(artist));
    }
}
