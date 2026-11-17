using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Writes a vocal part's lyrics as LRC, with or without the A2 extension that times each word. Written from the format's
/// published description (see THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// <para>Each phrase is a line, stamped with the time of its first lyric; lyrics outside every phrase are lines of their own.
/// The chart's symbols are taken off the syllables (<see cref="LyricSyllable"/>), a slide's <c>+</c> is dropped, and a
/// syllable that joins the next is written with no space after it.</para>
/// <para>With A2 each syllable has its own <c>&lt;mm:ss.xx&gt;</c> tag, and the line ends with one at the end of its last note.</para>
/// </remarks>
public static class LrcWriter
{
    /// <summary>The LRC for <paramref name="content"/>'s lyrics; empty when it has none.</summary>
    /// <param name="secondsOf">The time in the audio, in seconds, of a tick: the song's delay included.</param>
    /// <param name="includeWordTimes">Whether each syllable is timed (A2), not just each line.</param>
    public static string Write(VocalTrackContent content, Func<long, double> secondsOf, bool includeWordTimes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(secondsOf);
        var output = new StringBuilder();
        foreach (var line in Lines(content))
        {
            var syllables = line
                .Select(l => (l.Tick, Syllable: LyricSyllable.Of(l.Text)))
                .Where(static s => !s.Syllable.IsSlide && s.Syllable.Text.Length > 0)
                .ToList();
            if (syllables.Count == 0)
                continue;

            output.Append('[').Append(LyricTime.Lrc(secondsOf(syllables[0].Tick))).Append(']');
            for (int i = 0; i < syllables.Count; i++)
            {
                var (tick, syllable) = syllables[i];
                if (includeWordTimes)
                    output.Append('<').Append(LyricTime.Lrc(secondsOf(tick))).Append('>');
                output.Append(syllable.Text);
                if (!syllable.JoinsNext && i < syllables.Count - 1)
                    output.Append(' ');
            }

            if (includeWordTimes && EndOf(content, syllables[^1].Tick) is { } end && end > syllables[^1].Tick)
                output.Append('<').Append(LyricTime.Lrc(secondsOf(end))).Append('>');
            output.Append('\n');
        }

        return output.ToString();
    }

    // The lyrics of each phrase, then those outside any phrase, in order.
    private static List<List<VocalLyricEvent>> Lines(VocalTrackContent content)
    {
        var lines = new List<List<VocalLyricEvent>>();
        var phrases = content.Phrases.OrderBy(static p => p.Tick).ToList();
        int at = 0;

        // The phrase the line being filled is of, or -1 for lyrics outside every phrase.
        int current = -2;
        foreach (var lyric in content.Lyrics)
        {
            while (at < phrases.Count && phrases[at].EndTick <= lyric.Tick)
                at++;
            int phrase = at < phrases.Count && phrases[at].Tick <= lyric.Tick ? at : -1;
            if (phrase != current || lines.Count == 0)
            {
                lines.Add([]);
                current = phrase;
            }

            lines[^1].Add(lyric);
        }

        return lines;
    }

    // Where the note starting at the tick ends, or null when none starts there.
    private static long? EndOf(VocalTrackContent content, long tick)
    {
        long? end = null;
        foreach (var note in content.Notes)
        {
            if (note.Tick == tick)
                end = Math.Max(end ?? 0, note.EndTick);
            else if (note.Tick > tick)
                break;
        }

        return end;
    }
}
