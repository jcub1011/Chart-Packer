using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The edits of a vocal part's notes and lyrics the tools, the clipboard and Properties share (charter-spec C3 item 7),
/// each giving a new content.
/// </summary>
/// <remarks>
/// A lyric belongs to the notes that start on its tick: moving them takes it along, and removing them takes it away,
/// unless a note that stays still starts there. A lyric on no note's tick is left where it is.
/// </remarks>
public static class VocalEdits
{
    /// <summary>The lyric a slide's note has.</summary>
    public const string SlideText = "+";

    /// <summary>The symbol that marks a note spoken rather than sung.</summary>
    public const char SpokenSymbol = '#';

    // The symbols the games read after a syllable (LyricSyllable.Of), in any order.
    private const string Symbols = "-=#^*%$/";

    private const string PhrasesOverlap = "A vocal part's phrases can't overlap.";

    /// <summary>The notes of <paramref name="content"/> at <paramref name="places"/>, in tick order; places with no note are skipped.</summary>
    public static IReadOnlyList<VocalNoteEvent> NotesAt(VocalTrackContent content, IEnumerable<VocalNotePlace> places)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(places);
        var wanted = places.ToHashSet();
        return [.. content.Notes.Where(n => wanted.Contains(n.Place))];
    }

    /// <summary>The first lyric at <paramref name="tick"/>'s text, or null when there's none.</summary>
    public static string? LyricAt(VocalTrackContent content, long tick)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var lyric in content.Lyrics)
        {
            if (lyric.Tick == tick)
                return lyric.Text;
            if (lyric.Tick > tick)
                break;
        }

        return null;
    }

    /// <summary>Whether the note at <paramref name="tick"/> is a slide: its lyric is <see cref="SlideText"/>.</summary>
    public static bool IsSlide(VocalTrackContent content, long tick) => LyricAt(content, tick)?.Trim() == SlideText;

    /// <summary>Whether the note at <paramref name="tick"/> is spoken: its lyric is marked <c>#</c> or <c>^</c>.</summary>
    public static bool IsSpoken(VocalTrackContent content, long tick) =>
        LyricAt(content, tick) is { } text && LyricSyllable.Of(text).IsUnpitched;

    /// <summary>
    /// <paramref name="content"/> with <paramref name="note"/> added, in place of any note at its tick and pitch, and with
    /// <paramref name="lyric"/> as its lyric when it's given.
    /// </summary>
    public static VocalTrackContent Add(VocalTrackContent content, VocalNoteEvent note, string? lyric = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var added = content.With(notes: content.Notes.Where(n => n.Place != note.Place).Append(note));
        return lyric is null ? added : SetLyric(added, [note.Tick], lyric);
    }

    /// <summary>
    /// <paramref name="content"/> with the notes at <paramref name="places"/> taken out, and the lyrics on their ticks with
    /// them, unless a note left still starts there.
    /// </summary>
    public static VocalTrackContent Remove(VocalTrackContent content, IEnumerable<VocalNotePlace> places)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(places);
        var gone = places.ToHashSet();
        var left = content.Notes.Where(n => !gone.Contains(n.Place)).ToList();
        var starts = left.Select(static n => n.Tick).ToHashSet();
        var lyricsGone = gone.Select(static p => p.Tick).Where(t => !starts.Contains(t)).ToHashSet();
        return content.With(notes: left, lyrics: content.Lyrics.Where(l => !lyricsGone.Contains(l.Tick)));
    }

    /// <summary>
    /// <paramref name="content"/> with each note at <paramref name="places"/> changed by <paramref name="change"/>, its
    /// lyrics going with it to its new tick unless a note that stays still starts on the old one. A note left where a
    /// changed one lands is replaced by it, and its lyrics go with it.
    /// </summary>
    /// <param name="changed">Where the changed notes are now.</param>
    /// <exception cref="ArgumentOutOfRangeException">A note would be before the start, have no pitch the games sing, or a negative length.</exception>
    public static VocalTrackContent Change(
        VocalTrackContent content, IEnumerable<VocalNotePlace> places, Func<VocalNoteEvent, VocalNoteEvent> change, out IReadOnlyList<VocalNotePlace> changed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(change);
        var wanted = places.ToHashSet();
        var moving = content.Notes.Where(n => wanted.Contains(n.Place)).ToList();
        var to = moving.Select(change).ToList();
        changed = [.. to.Select(static n => n.Place)];

        var landed = changed.ToHashSet();
        var staying = content.Notes.Where(n => !wanted.Contains(n.Place) && !landed.Contains(n.Place)).ToList();
        var starts = staying.Select(static n => n.Tick).ToHashSet();

        // Each moved note's lyrics go where it goes; the lyrics of a note it replaces go.
        var moves = new Dictionary<long, long>();
        for (int i = 0; i < moving.Count; i++)
        {
            if (!starts.Contains(moving[i].Tick))
                moves.TryAdd(moving[i].Tick, to[i].Tick);
        }

        var replaced = content.Notes
            .Where(n => !wanted.Contains(n.Place) && landed.Contains(n.Place) && !starts.Contains(n.Tick) && !moves.ContainsKey(n.Tick))
            .Select(static n => n.Tick)
            .ToHashSet();
        var lyrics = content.Lyrics
            .Where(l => !replaced.Contains(l.Tick))
            .Select(l => moves.TryGetValue(l.Tick, out long tick) ? l with { Tick = tick } : l);
        return content.With(notes: staying.Concat(to), lyrics: lyrics);
    }

    /// <summary>
    /// <paramref name="content"/> with the lyric at each of <paramref name="ticks"/> set to <paramref name="text"/>: the
    /// lyrics there replaced by one, written as the first of them was; none, when <paramref name="text"/> is blank.
    /// </summary>
    public static VocalTrackContent SetLyric(VocalTrackContent content, IEnumerable<long> ticks, string text)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentNullException.ThrowIfNull(text);
        return SetLyrics(content, ticks.Distinct().Select(t => (t, text)));
    }

    /// <summary><paramref name="content"/> with each of <paramref name="lyrics"/> set as <see cref="SetLyric"/> sets one.</summary>
    public static VocalTrackContent SetLyrics(VocalTrackContent content, IEnumerable<(long Tick, string Text)> lyrics)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(lyrics);
        var set = new Dictionary<long, string>();
        foreach (var (tick, text) in lyrics)
            set[tick] = text.Trim();
        if (set.Count == 0)
            return content;

        var result = new List<VocalLyricEvent>(content.Lyrics.Count + set.Count);
        var done = new HashSet<long>();
        foreach (var lyric in content.Lyrics)
        {
            if (!set.TryGetValue(lyric.Tick, out string? text))
            {
                result.Add(lyric);
                continue;
            }

            // The first at the tick takes the text; the rest go.
            if (done.Add(lyric.Tick) && text.Length > 0)
                result.Add(lyric with { Text = text });
        }

        foreach (var (tick, text) in set)
        {
            if (!done.Contains(tick) && text.Length > 0)
                result.Add(new VocalLyricEvent(tick, text));
        }

        return content.With(lyrics: result);
    }

    /// <summary>
    /// <paramref name="content"/> with the notes at <paramref name="ticks"/> made slides (their lyric <c>+</c>), or, when
    /// <paramref name="on"/> is false, a slide's lyric taken away.
    /// </summary>
    public static VocalTrackContent SetSlide(VocalTrackContent content, IEnumerable<long> ticks, bool on)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ticks);
        var list = ticks.Distinct().ToList();
        return on
            ? SetLyric(content, list, SlideText)
            : SetLyric(content, list.Where(t => IsSlide(content, t)), string.Empty);
    }

    /// <summary>
    /// <paramref name="content"/> with the notes at <paramref name="ticks"/> marked spoken (<c>#</c> after the syllable),
    /// or, when <paramref name="on"/> is false, their <c>#</c> and <c>^</c> taken off, keeping their other symbols. A slide
    /// is left as it is.
    /// </summary>
    public static VocalTrackContent SetSpoken(VocalTrackContent content, IEnumerable<long> ticks, bool on)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ticks);
        var lyrics = new List<(long, string)>();
        foreach (long tick in ticks.Distinct())
        {
            string text = LyricAt(content, tick)?.Trim() ?? string.Empty;
            if (text == SlideText || IsSpoken(content, tick) == on)
                continue;
            lyrics.Add((tick, on ? text + SpokenSymbol : Unspoken(text)));
        }

        return SetLyrics(content, lyrics);
    }

    /// <summary>
    /// The syllables of a typed line of lyrics: each word, split after each hyphen inside it, so <c>Hel-lo world</c> is
    /// <c>Hel-</c>, <c>lo</c> and <c>world</c>. Every symbol typed is kept.
    /// </summary>
    public static IReadOnlyList<string> SplitLyrics(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var syllables = new List<string>();
        foreach (string word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int start = 0;
            for (int i = 0; i < word.Length - 1; i++)
            {
                if (word[i] is '-' or '=' && !Symbols.Contains(word[i + 1], StringComparison.Ordinal))
                {
                    syllables.Add(word[start..(i + 1)]);
                    start = i + 1;
                }
            }

            syllables.Add(word[start..]);
        }

        return syllables;
    }

    /// <summary>
    /// <paramref name="content"/> with <paramref name="syllables"/> given in order to the notes at
    /// <paramref name="places"/>, from the earliest, a slide skipped, each replacing the note's lyric.
    /// </summary>
    /// <param name="left">How many syllables there were no note for.</param>
    public static VocalTrackContent SpreadLyrics(VocalTrackContent content, IEnumerable<VocalNotePlace> places, IReadOnlyList<string> syllables, out int left)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(syllables);
        var ticks = NotesAt(content, places).Select(static n => n.Tick).Distinct().Where(t => !IsSlide(content, t)).ToList();
        int count = Math.Min(ticks.Count, syllables.Count);
        left = syllables.Count - count;
        return SetLyrics(content, Enumerable.Range(0, count).Select(i => (ticks[i], syllables[i])));
    }

    /// <summary><paramref name="content"/> with <paramref name="phrase"/> added.</summary>
    /// <exception cref="ArgumentException">It would overlap a phrase the part has.</exception>
    public static VocalTrackContent AddPhrase(VocalTrackContent content, VocalPhraseEvent phrase)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WithPhrases(content, content.Phrases.Append(phrase));
    }

    /// <summary>Whether any two of <paramref name="phrases"/> overlap, which a part's phrases never do.</summary>
    public static bool Overlap(IEnumerable<VocalPhraseEvent> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        long end = long.MinValue;
        foreach (var phrase in phrases.OrderBy(static p => p.Tick))
        {
            if (phrase.Tick < end)
                return true;
            end = Math.Max(end, phrase.EndTick);
        }

        return false;
    }

    /// <summary><paramref name="content"/> with <paramref name="phrases"/> taken out.</summary>
    public static VocalTrackContent RemovePhrases(VocalTrackContent content, IEnumerable<VocalPhraseEvent> phrases)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(phrases);
        var gone = phrases.ToHashSet();
        return content.With(phrases: content.Phrases.Where(p => !gone.Contains(p)));
    }

    /// <summary>
    /// <paramref name="content"/> with each of <paramref name="phrases"/> changed by <paramref name="change"/>.
    /// </summary>
    /// <param name="changed">The phrases as they are now.</param>
    /// <exception cref="ArgumentException">A phrase would overlap another.</exception>
    public static VocalTrackContent ChangePhrases(
        VocalTrackContent content, IEnumerable<VocalPhraseEvent> phrases, Func<VocalPhraseEvent, VocalPhraseEvent> change, out IReadOnlyList<VocalPhraseEvent> changed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(change);
        var wanted = phrases.ToHashSet();
        var to = content.Phrases.Where(wanted.Contains).Select(change).ToList();
        changed = to;
        return WithPhrases(content, content.Phrases.Where(p => !wanted.Contains(p)).Concat(to));
    }

    // The content with phrases, which mustn't overlap: With would cut them short where they do.
    private static VocalTrackContent WithPhrases(VocalTrackContent content, IEnumerable<VocalPhraseEvent> phrases)
    {
        var list = phrases.ToList();
        return Overlap(list) ? throw new ArgumentException(PhrasesOverlap) : content.With(phrases: list);
    }

    // The text with # and ^ taken out of the symbols after its syllable.
    private static string Unspoken(string text)
    {
        int end = text.Length;
        while (end > 0 && Symbols.Contains(text[end - 1], StringComparison.Ordinal))
            end--;
        return text[..end] + text[end..].Replace("#", "", StringComparison.Ordinal).Replace("^", "", StringComparison.Ordinal);
    }
}
