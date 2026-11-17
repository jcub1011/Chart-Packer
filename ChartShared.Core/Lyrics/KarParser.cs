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
/// <para>A karaoke file doesn't say which notes are sung, so the melody is looked for: the track, drums aside, with the
/// most notes starting on a syllable, within a 128th note. When it has one for at least half the syllables, each
/// syllable with one is sung at its pitch, the highest starting there, and held to its end. A melody note after it, before
/// the next syllable and following straight on from the one before, carries the syllable on: a slide when its pitch is
/// another, otherwise it only holds it longer. A syllable with no note, or every syllable when no track is the melody,
/// has no pitch.</para>
/// </remarks>
public static class KarParser
{
    private const byte TextType = 0x01;
    private const byte LyricType = 0x05;
    private const int DrumChannel = 9;

    // Of the syllables, how many a track must have a note on to be the melody.
    private const double MelodyShare = 0.5;

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

        var syllables = best.OrderBy(static s => s.Tick).ToList();
        long tolerance = Math.Max(1, division / 32);
        var melody = Melody(bytes, tracks, syllables, tolerance);

        var lines = new List<LyricLine>();
        var words = new List<LyricWord>();
        bool joinsNext = false;
        TimeSpan? lineStart = null;

        for (int s = 0; s < syllables.Count; s++)
        {
            var (tick, raw) = syllables[s];
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

            var at = Time(tick);
            lineStart ??= at;
            int last = words.FindLastIndex(static w => !w.IsSlide);
            if (joinsNext && !char.IsWhiteSpace(text[0]) && last >= 0 && !words[last].Text.EndsWith('-'))
                words[last] = words[last] with { Text = words[last].Text + "-" };

            int note = melody is null ? -1 : NoteAt(melody, tick, tolerance);
            if (note < 0)
            {
                words.Add(new LyricWord(word, at));
            }
            else
            {
                var sung = melody![note];
                words.Add(new LyricWord(word, at, Time(sung.End), sung.Pitch));

                // The notes it's carried on over, up to the next syllable.
                long next = s + 1 < syllables.Count ? syllables[s + 1].Tick - tolerance : long.MaxValue;
                long end = sung.End;
                for (int n = note + 1; n < melody.Count && melody[n].Start < next && melody[n].Start - end <= division / 2; n++)
                {
                    var on = melody[n];
                    if (on.Pitch != words[^1].Pitch)
                        words.Add(new LyricWord(LyricWord.SlideText, Time(on.Start), Time(on.End), on.Pitch));
                    else
                        words[^1] = words[^1] with { End = Time(on.End) };
                    end = on.End;
                }
            }

            joinsNext = !char.IsWhiteSpace(text[^1]);
        }

        EndLine();
        return LyricDocument.Of(lines);

        TimeSpan Time(long tick) => TimeSpan.FromSeconds(tempo.SecondsAt(tick));

        void EndLine()
        {
            if (words.Count > 0 && lineStart is { } start)
                lines.Add(new LyricLine(start, null, [.. words]));
            words.Clear();
            lineStart = null;
            joinsNext = false;
        }
    }

    // The melody: of the tracks' notes, drums aside, those of the track with a note on the most syllables, the highest
    // at each tick, in order; null when no track has one on enough of them.
    private static List<(long Start, long End, int Pitch)>? Melody(
        byte[] bytes, List<List<MidiEvent>> tracks, List<(long Tick, string Text)> syllables, long tolerance)
    {
        var sung = syllables.Where(static s => s.Text.Trim().Length > 0 && s.Text.Trim() is not ("/" or "\\")).Select(static s => s.Tick).ToList();
        if (sung.Count == 0)
            return null;

        List<(long Start, long End, int Pitch)>? best = null;
        int bestCount = 0;
        foreach (var events in tracks)
        {
            var notes = Notes(bytes, events);
            if (notes.Count == 0)
                continue;
            int count = sung.Count(t => NoteAt(notes, t, tolerance) >= 0);
            if (count > bestCount)
                (best, bestCount) = (notes, count);
        }

        return bestCount >= sung.Count * MelodyShare ? best : null;
    }

    // A track's notes, drums aside, the highest at each tick, in order.
    private static List<(long Start, long End, int Pitch)> Notes(byte[] bytes, List<MidiEvent> events)
    {
        var notes = new List<(long Start, long End, int Pitch)>();
        var open = new Dictionary<(int Channel, int Pitch), long>();
        foreach (var e in events)
        {
            if (e.IsMeta || e.IsSysex || e.Kind is not (0x80 or 0x90) || e.DataLength < 2)
                continue;
            int channel = e.Status & 0x0F;
            if (channel == DrumChannel)
                continue;

            int pitch = bytes[e.DataStart];
            bool on = e.Kind == 0x90 && bytes[e.DataStart + 1] > 0;
            if (open.Remove((channel, pitch), out long start) && e.Tick > start)
                notes.Add((start, e.Tick, pitch));
            if (on)
                open[(channel, pitch)] = e.Tick;
        }

        return [.. notes.GroupBy(static n => n.Start).Select(static g => g.MaxBy(static n => n.Pitch)).OrderBy(static n => n.Start)];
    }

    // The index of the note of notes, in order, starting nearest tick, no further from it than tolerance; -1 when none does.
    private static int NoteAt(List<(long Start, long End, int Pitch)> notes, long tick, long tolerance)
    {
        int lo = 0, hi = notes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (notes[mid].Start < tick - tolerance)
                lo = mid + 1;
            else
                hi = mid;
        }

        int found = -1;
        for (int i = lo; i < notes.Count && notes[i].Start <= tick + tolerance; i++)
        {
            if (found < 0 || Math.Abs(notes[i].Start - tick) < Math.Abs(notes[found].Start - tick))
                found = i;
        }

        return found;
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
