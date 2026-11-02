using LyricsFloat.Core.Lyrics;

namespace LyricsFloat.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_lines_in_time_order()
    {
        var lines = LrcParser.Parse("[00:05.50] second line\n[00:01.00] first line");

        Assert.Equal(
            [new LyricLine(TimeSpan.FromSeconds(1), "first line"), new LyricLine(TimeSpan.FromSeconds(5.5), "second line")],
            lines);
    }

    [Theory]
    [InlineData("[01:02] x", 62_000)]
    [InlineData("[01:02.5] x", 62_500)]
    [InlineData("[01:02.50] x", 62_500)]
    [InlineData("[01:02.500] x", 62_500)]
    [InlineData("[01:02:50] x", 62_500)]
    public void Reads_all_fraction_formats(string lrc, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), LrcParser.Parse(lrc).Single().Time);
    }

    [Fact]
    public void Expands_lines_with_several_time_tags()
    {
        var lines = LrcParser.Parse("[00:10.00][00:30.00] chorus line\n[00:20.00] verse line");

        Assert.Equal(["chorus line", "verse line", "chorus line"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Skips_metadata_and_untimed_text()
    {
        var lines = LrcParser.Parse("[ar:Someone]\n[ti:Something]\nnot a lyric\n[00:01.00] only line");

        Assert.Equal("only line", Assert.Single(lines).Text);
    }

    [Fact]
    public void Keeps_empty_lines_as_breaks()
    {
        var lines = LrcParser.Parse("[00:01.00] a line\n[00:04.00]\n[00:09.00] another line");

        Assert.Equal(["a line", "", "another line"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Applies_offset_tag_and_never_goes_negative()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:00.20] early\n[00:02.00] later");

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1.5)], lines.Select(l => l.Time));
    }

    [Fact]
    public void Strips_word_level_timing()
    {
        var line = LrcParser.Parse("[00:01.00] <00:01.00>one <00:01.50>two").Single();

        Assert.Equal("one two", line.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_nothing_for_blank_input(string? lrc)
    {
        Assert.Empty(LrcParser.Parse(lrc));
    }
}
