using System.Globalization;
using System.Text.RegularExpressions;

namespace LyricsFloat.Core.Lyrics;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Parses LRC lyrics ("[mm:ss.xx] text") into time-ordered lines.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    // Word-level timing from "enhanced" LRC: <00:12.34>
    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return [];

        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var offsetMatch = OffsetTag().Match(line);
            if (offsetMatch.Success)
            {
                offset = TimeSpan.FromMilliseconds(int.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            // A line may start with several time tags: "[00:12.00][01:30.00] text"
            var stamps = new List<TimeSpan>();
            var pos = 0;
            for (var m = TimeTag().Match(line); m.Success && m.Index == pos; m = m.NextMatch())
            {
                stamps.Add(ToTime(m));
                pos += m.Length;
            }
            if (stamps.Count == 0) continue; // metadata such as [ar:...] or stray text

            var text = WordTag().Replace(line[pos..], "").Trim();
            lines.AddRange(stamps.Select(t => new LyricLine(t, text)));
        }

        // A positive LRC offset means the lyrics should show earlier.
        return lines
            .Select(l => l with { Time = l.Time - offset < TimeSpan.Zero ? TimeSpan.Zero : l.Time - offset })
            .OrderBy(l => l.Time)
            .ToList();
    }

    private static TimeSpan ToTime(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups[3].Value;
        // ".5" is tenths, ".50" hundredths, ".500" milliseconds.
        var millis = fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) * (int)Math.Pow(10, 3 - fraction.Length);
        return new TimeSpan(0, 0, minutes, seconds, millis);
    }
}
