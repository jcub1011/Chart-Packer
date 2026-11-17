using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the lyrics of a <c>.kar</c> karaoke file: a standard MIDI file whose syllables are text or lyric events. Written
/// from the Standard MIDI File description and the karaoke convention (see THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// <para>Each syllable is an event at the tick it's sung on. The track with the most lyric events is read, or, when none have
/// lyric events, the one with the most text events, those starting <c>@</c> (the file's header information) aside.
/// A <c>/</c> or <c>\</c> at the start of a syllable begins a new line, as does a carriage return or line feed.</para>
/// <para>As in an UltraStar file, a syllable that ends in a space, or one the next begins with a space, ends a word;
/// otherwise the next carries it on, so the first ends in a hyphen. Ticks are timed by the file's tempo changes.</para>
/// </remarks>
public static class KarParser
{
    private const byte TextType = 0x01;
    private const byte LyricType = 0x05;

    /// <summary>Reads the karaoke file <paramref name="bytes"/>.</summary>
    /// <exception cref="ChartFormatException">It isn't a MIDI file, or has no lyrics.</exception>
    public static LyricDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var (_, division, chunks) = MidiFile.ReadChunks(bytes);
        var tracks = chunks.Where(static c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(bytes, c)).ToList();
        var tempo = MidiChartReader.TempoOf(bytes, division, tracks);

        // Per track, its syllables: lyric events if it has any, otherwise text events.
        List<(long Tick, string Text)>? best = null;
        bool bestIsLyric = false;
        foreach (var events in tracks)
        {
            var lyrics = Syllables(bytes, events, LyricType);
            bool isLyric = lyrics.Count > 0;
            var found = isLyric ? lyrics : Syllables(bytes, events, TextType);
            if (found.Count == 0)
                continue;
            if (best is null || (isLyric && !bestIsLyric) || (isLyric == bestIsLyric && found.Count > best.Count))
                (best, bestIsLyric) = (found, isLyric);
        }

        if (best is null)
            throw new ChartFormatException("The karaoke file has no lyrics.");

        var lines = new List<LyricLine>();
        var words = new List<LyricWord>();
        bool joinsNext = false;
        TimeSpan? lineStart = null;

        foreach (var (tick, raw) in best.OrderBy(static s => s.Tick))
        {
            string text = raw;
            bool breakBefore = false;
            if (text.StartsWith('/') || text.StartsWith('\\'))
            {
                breakBefore = true;
                text = text[1..];
            }

            if (text.Contains('\r') || text.Contains('\n'))
            {
                // A line feed in a syllable ends the line after it, or before it when it comes first.
                breakBefore |= text.TrimStart(' ').StartsWith('\r') || text.TrimStart(' ').StartsWith('\n');
                text = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            }

            if (breakBefore)
                EndLine();

            string word = text.Trim();
            if (word.Length == 0)
            {
                joinsNext = false;
                continue;
            }

            var at = TimeSpan.FromSeconds(tempo.SecondsAt(tick));
            lineStart ??= at;
            if (joinsNext && !char.IsWhiteSpace(text[0]) && words.Count > 0 && !words[^1].Text.EndsWith('-'))
                words[^1] = words[^1] with { Text = words[^1].Text + "-" };
            words.Add(new LyricWord(word, at));
            joinsNext = !char.IsWhiteSpace(text[^1]);
        }

        EndLine();
        return LyricDocument.Of(lines);

        void EndLine()
        {
            if (words.Count > 0 && lineStart is { } start)
                lines.Add(new LyricLine(start, null, [.. words]));
            words.Clear();
            lineStart = null;
            joinsNext = false;
        }
    }

    private static List<(long Tick, string Text)> Syllables(byte[] bytes, List<MidiEvent> events, byte type)
    {
        var found = new List<(long, string)>();
        foreach (var e in events)
        {
            if (e.IsMeta && MidiFile.TextOf(bytes, e, type) is { } text && !text.StartsWith('@') && !text.TrimStart().StartsWith('['))
                found.Add((e.Tick, text));
        }

        return found;
    }
}
