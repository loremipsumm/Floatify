namespace LyricsFloat.Core.Lyrics;

/// <summary>Lyric lines on a time axis, with lookup of the line playing at a given moment.</summary>
public sealed class LyricsTimeline
{
    private LyricsTimeline(IReadOnlyList<LyricLine> lines, bool isApproximate)
    {
        Lines = lines;
        IsApproximate = isApproximate;
    }

    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>True when built from unsynced lyrics, so timings are only a rough guess.</summary>
    public bool IsApproximate { get; }

    public static LyricsTimeline FromSynced(IReadOnlyList<LyricLine> lines) => new(lines, isApproximate: false);

    /// <summary>Spreads unsynced lines evenly over the track so something sensible is on screen.</summary>
    public static LyricsTimeline FromPlain(string plain, TimeSpan duration)
    {
        var texts = plain.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (texts.Count == 0 || duration <= TimeSpan.Zero) return new([], isApproximate: true);

        var step = duration / texts.Count;
        return new(texts.Select((text, i) => new LyricLine(step * i, text)).ToList(), isApproximate: true);
    }

    /// <summary>Prefers synced lyrics; returns null when there is nothing to show.</summary>
    public static LyricsTimeline? FromEntry(LyricsEntry entry, TimeSpan duration)
    {
        if (entry.SyncedLyrics is not null)
        {
            var lines = LrcParser.Parse(entry.SyncedLyrics);
            if (lines.Count > 0) return FromSynced(lines);
        }
        if (entry.PlainLyrics is not null)
        {
            var timeline = FromPlain(entry.PlainLyrics, duration);
            if (timeline.Lines.Count > 0) return timeline;
        }
        return null;
    }

    /// <summary>Index of the last line starting at or before <paramref name="position"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}
