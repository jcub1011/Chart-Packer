using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Charting.Editing;

/// <summary>How an imported set of lyrics goes in with the part's own.</summary>
public enum LyricImportMode
{
    /// <summary>The part's notes, lyrics and phrases are taken out first. Its star power is kept.</summary>
    Replace,

    /// <summary>The part keeps what it has, and each imported word is added where it doesn't fall on a note the part has.</summary>
    Merge,
}

/// <summary>What an import did.</summary>
/// <param name="Done">Whether the part was changed, as one undoable step.</param>
/// <param name="Message">Why it wasn't, or null.</param>
/// <param name="Words">How many words were added to the part.</param>
/// <param name="Skipped">How many words were left out: before the start of the song, on the same tick as another, or, merging, on a note the part has.</param>
public readonly record struct LyricImportResult(bool Done, string? Message, int Words, int Skipped);

/// <summary>
/// Puts lyrics read from a file into a vocal part (charter-spec C3 item 7): every word a note, sung at its pitch when the
/// file gave one and spoken otherwise, one phrase to a line.
/// </summary>
/// <remarks>
/// <para>A word with a pitch is a note at it, its text as it is; a slide (<see cref="LyricWord.IsSlide"/>) is a note with the
/// lyric <c>+</c>, which the games join to the note before. A word with none is a note at <see cref="VocalLane.SpokenPitch"/>
/// with its text marked <c>#</c>. Each is held until the next word starts, or the end the file gave.</para>
/// <para>A part's pitches are moved by whole octaves into the range the games sing in, 36 to 84: first all together, by
/// the octaves that put the most of them in it, then any still out of it each on its own.</para>
/// <para>A word with no time of its own shares the time before the next timed word, or the end of its
/// line, with those beside it, by how many letters each has. A line with no end of its own ends where the next begins, but
/// no more than a second, and about a seventh of a second a letter, after it starts, so a gap in the singing isn't filled by the last word.</para>
/// <para>The file's times are the audio's; the song's delay is taken off them (<see cref="EditorSession.TickAt"/>).</para>
/// </remarks>
public static class LyricImport
{
    // How long a word lasts when nothing says: the last of a song, or the last before a gap.
    private const double MinimumLineSeconds = 1.0;
    private const double SecondsPerLetter = 0.15;
    private const double MinimumWordSeconds = 0.05;
    private const int Octave = 12;

    /// <summary>
    /// Puts <paramref name="document"/> into the vocal part <paramref name="track"/> as one undoable step, adding the part
    /// first when the chart hasn't it. A refusal is said in the session's status, and returned.
    /// </summary>
    public static LyricImportResult Apply(EditorSession session, VocalTrack track, LyricDocument document, LyricImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Apply(session, [(track, document)], mode);
    }

    /// <summary>
    /// Puts each document of <paramref name="parts"/> into its vocal part, all as one undoable step, adding a part first
    /// when the chart hasn't it: a duet's singers into two harmonies. A refusal is said in the session's status, and returned.
    /// </summary>
    public static LyricImportResult Apply(EditorSession session, IReadOnlyList<(VocalTrack Track, LyricDocument Document)> parts, LyricImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        session.Tools.Cancel();
        if (parts.Sum(static p => p.Document.WordCount) == 0)
            return new(false, NothingToImport, 0, 0);
        if (parts.Any(p => VocalTracks.IndexIn(session.Chart, p.Track) >= 0 && session.VocalContentOf(p.Track) is null))
            return Refused(session, VocalsUnreadable);

        int words = 0, skipped = 0;
        bool changed = false;
        var steps = new List<Func<(IChartCommand?, string?)>>();
        foreach (var (track, document) in parts.Where(static p => p.Document.WordCount > 0))
        {
            if (VocalTracks.IndexIn(session.Chart, track) < 0)
                steps.Add(() => (new AddVocalPart(track), null));
            steps.Add(() =>
            {
                if (session.VocalContentOf(track) is not { } content)
                    return (null, VocalsUnreadable);
                var (to, placed, left) = Build(content, document, mode, session.TickAt);
                (words, skipped) = (words + placed, skipped + left);
                if (placed == 0 || ReferenceEquals(to, content))
                    return (null, null);
                changed = true;
                return (new ChangeVocals(track, to), null);
            });
        }

        // Refused as a whole when no part took a word.
        steps.Add(() => (null, words > 0 ? null : skipped > 0 ? NoRoom : NothingToImport));

        if (!session.ExecuteAsOne(steps))
            return new(false, session.Status, 0, skipped);
        return new(changed, changed ? null : NothingToImport, changed ? words : 0, skipped);
    }

    /// <summary>
    /// The part <paramref name="content"/> with <paramref name="document"/> put into it as <paramref name="mode"/> says.
    /// </summary>
    /// <param name="tickAt">The tick of a time in the audio, in seconds, delay taken off.</param>
    /// <returns>The part, how many words it has of the document's, and how many were left out.</returns>
    internal static (VocalTrackContent Content, int Placed, int Skipped) Build(
        VocalTrackContent content, LyricDocument document, LyricImportMode mode, Func<double, double> tickAt)
    {
        var timed = Resolve(document);
        var sorted = timed.OrderBy(static w => w.Start).ThenBy(static w => w.Line).ToList();
        var fold = Fold(timed.Where(static w => w.Pitch is not null).Select(static w => w.Pitch!.Value));

        // Each word at its tick, with the tick it's let go at, no later than the next word starts.
        var words = new List<(long Tick, long End, string Text, int? Pitch, int Line)>();
        int skipped = 0;
        long lastTick = -1;
        foreach (var (word, i) in sorted.Select(static (w, i) => (w, i)))
        {
            long tick = (long)Math.Round(tickAt(word.Start));
            long end = (long)Math.Round(tickAt(word.End));
            // A word that ends before the song starts has no place; one that starts before it starts at it.
            tick = Math.Max(0, tick);
            if (end <= 0 || tick <= lastTick)
            {
                skipped++;
                continue;
            }

            long next = i + 1 < sorted.Count ? (long)Math.Round(tickAt(sorted[i + 1].Start)) : long.MaxValue;
            end = Math.Max(tick + 1, Math.Min(end, next));
            words.Add((tick, end, word.Text, word.Pitch is { } pitch ? fold(pitch) : null, word.Line));
            lastTick = tick;
        }

        var notes = new List<VocalNoteEvent>();
        var lyrics = new List<VocalLyricEvent>();
        var placed = new List<(long Tick, long End, int Line)>();
        var existing = mode == LyricImportMode.Merge ? content.Notes.OrderBy(static n => n.Tick).ToList() : [];
        int lastLine = -1;
        foreach (var (tick, end, text, pitch, line) in words)
        {
            // A slide carries on the note before it, so it goes in only straight after one of its line's.
            bool slide = text == LyricWord.SlideText;
            if (slide && lastLine != line)
            {
                skipped++;
                lastLine = -1;
                continue;
            }

            long until = end;
            if (existing.Count > 0)
            {
                if (existing.Any(n => n.Tick <= tick && n.EndTick > tick) || existing.Any(n => n.Tick == tick))
                {
                    skipped++;
                    lastLine = -1;
                    continue;
                }

                // Held no further than the next note the part has.
                if (existing.Where(n => n.Tick > tick).Select(static n => (long?)n.Tick).Min() is { } after)
                    until = Math.Min(until, after);
            }

            if (until <= tick)
            {
                skipped++;
                lastLine = -1;
                continue;
            }

            notes.Add(new VocalNoteEvent(tick, until - tick, pitch ?? VocalLane.SpokenPitch));
            lyrics.Add(new VocalLyricEvent(tick, pitch is not null ? text : VocalEdits.WithSpokenMarks(text, VocalEdits.SpokenSymbol.ToString())));
            placed.Add((tick, until, line));
            lastLine = line;
        }

        var phrases = placed
            .GroupBy(static p => p.Line)
            .Select(static g => new VocalPhraseEvent(g.Min(static p => p.Tick), g.Max(static p => p.End) - g.Min(static p => p.Tick)))
            .OrderBy(static p => p.Tick)
            .ToList();

        VocalTrackContent to;
        if (mode == LyricImportMode.Replace)
        {
            to = content.With(notes: notes, lyrics: lyrics, phrases: phrases);
        }
        else
        {
            // A phrase that would overlap one the part has is left out; its words join the part's phrase.
            var kept = phrases.Where(p => !content.Phrases.Any(c => p.Tick < c.EndTick && c.Tick < p.EndTick));
            to = VocalEdits.SetLyrics(content.With(notes: content.Notes.Concat(notes), phrases: content.Phrases.Concat(kept)),
                lyrics.Select(static l => (l.Tick, l.Text)));
        }

        return (to, placed.Count, skipped);
    }

    // Takes each pitch by whole octaves into the range the games sing in: all of them by the octaves that put the most in
    // it, the fewest on a tie, then each still out of it on its own.
    private static Func<int, int> Fold(IEnumerable<int> pitches)
    {
        var all = pitches.ToList();
        int shift = 0, most = -1;
        foreach (int octaves in (ReadOnlySpan<int>)[0, -1, 1, -2, 2, -3, 3, -4, 4])
        {
            int inRange = all.Count(p => VocalTrackContent.IsSung(p + (octaves * Octave)));
            if (inRange > most)
                (shift, most) = (octaves * Octave, inRange);
        }

        return pitch =>
        {
            pitch += shift;
            while (pitch < VocalTrackContent.LowestPitch)
                pitch += Octave;
            while (pitch > VocalTrackContent.HighestPitch)
                pitch -= Octave;
            return pitch;
        };
    }

    // Each word of the document with the time it starts and ends at, in seconds, and its pitch, if it has one.
    private static List<(string Text, double Start, double End, int? Pitch, int Line)> Resolve(LyricDocument document)
    {
        var result = new List<(string, double, double, int?, int)>();
        var lines = document.Lines;
        for (int l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var words = line.Words
                .Select(static w => (Text: w.IsSlide ? LyricWord.SlideText : Sanitize(w.Text), w.Start, w.End, w.Pitch))
                .Where(static w => w.Text.Length > 0)
                .SkipWhile(static w => w.Text == LyricWord.SlideText && w.Pitch is not null)
                .ToList();
            if (words.Count == 0)
                continue;

            double lineStart = line.Start.TotalSeconds;
            double? nextStart = l + 1 < lines.Count ? lines[l + 1].Start.TotalSeconds : null;
            int letters = words.Sum(static w => Weight(w.Text));
            double anchor = Math.Max(lineStart, words.Where(static w => w.Start is not null).Select(static w => w.Start!.Value.TotalSeconds).DefaultIfEmpty(lineStart).Max());
            double limit = line.End?.TotalSeconds
                ?? Math.Min(nextStart ?? double.MaxValue, anchor + Math.Max(MinimumLineSeconds, letters * SecondsPerLetter));
            limit = Math.Max(limit, anchor + MinimumWordSeconds);

            // The start of each word: its own, or shared out over the run of untimed words it's in.
            var starts = new double[words.Count];
            int i = 0;
            while (i < words.Count)
            {
                if (words[i].Start is { } own)
                {
                    starts[i] = Math.Max(own.TotalSeconds, i > 0 ? starts[i - 1] : lineStart);
                    i++;
                    continue;
                }

                int run = i;
                while (i < words.Count && words[i].Start is null)
                    i++;
                double from = run == 0 ? lineStart : Math.Max(starts[run - 1], words[run - 1].End?.TotalSeconds ?? starts[run - 1]);
                double to = i < words.Count ? words[i].Start!.Value.TotalSeconds : limit;
                to = Math.Max(to, from + (MinimumWordSeconds * (i - run)));
                double total = Enumerable.Range(run, i - run).Sum(k => (double)Weight(words[k].Text));
                double at = from;
                for (int k = run; k < i; k++)
                {
                    starts[k] = at;
                    at += (to - from) * Weight(words[k].Text) / total;
                }
            }

            // Each word is held to its own end, if the file gave one, and no later than the next starts.
            for (int k = 0; k < words.Count; k++)
            {
                double next = k + 1 < words.Count ? starts[k + 1] : limit;
                double end = Math.Min(words[k].End?.TotalSeconds ?? next, next);
                result.Add((words[k].Text, starts[k], Math.Max(end, starts[k] + MinimumWordSeconds), words[k].Pitch, l));
            }
        }

        return result;
    }

    private static int Weight(string text) => Math.Max(1, text.Count(char.IsLetterOrDigit));

    // A word as a chart's syllable: the symbols the games read at its end, and a lone plus, which makes a slide, are taken
    // off. A slide the file gave (LyricWord.IsSlide) isn't put through this.
    private static string Sanitize(string text)
    {
        string word = text.Trim().TrimStart('$').TrimEnd('#', '^', '*', '%', '$', '/', '=');
        return word is "+" or "-" ? string.Empty : word;
    }

    private static LyricImportResult Refused(EditorSession session, string message)
    {
        session.Status = message;
        return new(false, message, 0, 0);
    }

    private const string VocalsUnreadable = "This vocal track can't be read, so it's kept as it is.";
    private const string NothingToImport = "There were no lyrics to import.";
    private const string NoRoom = "Every word of the lyrics falls where the part already has a note, or before the start of the song.";
}
