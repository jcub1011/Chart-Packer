namespace ChartShared.Core.Lyrics;

/// <summary>A word, or syllable, of a lyric line.</summary>
/// <param name="Text">The text, with no spaces around it. On a pitched word, a lone <c>+</c> is a slide: the word before carried on to a new pitch.</param>
/// <param name="Start">When it starts in the audio, or null when the format gives no time for it, so it's worked out from its line.</param>
/// <param name="End">When it ends, or null when the format doesn't say.</param>
/// <param name="Pitch">The MIDI pitch it's sung at, which may be outside the range a chart sings in, or null when the format gives none or the word is spoken.</param>
public sealed record LyricWord(string Text, TimeSpan? Start = null, TimeSpan? End = null, int? Pitch = null)
{
    /// <summary>The text a slide has.</summary>
    public const string SlideText = "+";

    /// <summary>Whether the word is a slide: pitched, with the text <see cref="SlideText"/>.</summary>
    public bool IsSlide => Pitch is not null && Text == SlideText;
}

/// <summary>A line of lyrics: a phrase.</summary>
/// <param name="Start">When the line starts in the audio.</param>
/// <param name="End">When the line ends, or null when the format doesn't say; the next line's start is then taken.</param>
/// <param name="Words">The line's words, in order.</param>
/// <param name="Singer">Which singer of a duet sings it, from 1; a song for one singer has every line sung by singer 1.</param>
public sealed record LyricLine(TimeSpan Start, TimeSpan? End, IReadOnlyList<LyricWord> Words, int Singer = 1)
{
    /// <summary>A line of <paramref name="text"/>, split into words at its spaces.</summary>
    public static LyricLine OfText(TimeSpan start, TimeSpan? end, string text) =>
        new(start, end, [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(static w => new LyricWord(w))]);
}

/// <summary>
/// Lyrics read from a file, whatever it was: lines of words with the times, and pitches, the format gave, in the audio's
/// time, before any song delay. A word with no time of its own is given one by <see cref="Charting.Editing.LyricImport"/>.
/// </summary>
/// <param name="Lines">The lines, by start time; a duet's singers' lines are among each other.</param>
public sealed record LyricDocument(IReadOnlyList<LyricLine> Lines)
{
    /// <summary>How many words the document has.</summary>
    public int WordCount => Lines.Sum(static l => l.Words.Count);

    /// <summary>Whether any word has a time of its own, which <see cref="WithoutWordTimes"/> takes away.</summary>
    public bool HasWordTimes => Lines.Any(static l => l.Words.Any(static w => w.Start is not null));

    /// <summary>Whether any word has a pitch, which <see cref="WithoutPitches"/> takes away.</summary>
    public bool HasPitches => Lines.Any(static l => l.Words.Any(static w => w.Pitch is not null));

    /// <summary>The singers the lines are sung by, in order: just 1, unless it's a duet.</summary>
    public IReadOnlyList<int> Singers => [.. Lines.Select(static l => l.Singer).Distinct().Order()];

    /// <summary>The lines <paramref name="singer"/> sings, as a document of their own.</summary>
    public LyricDocument ForSinger(int singer) => new([.. Lines.Where(l => l.Singer == singer)]);

    /// <summary>
    /// The document with each word's pitch taken away, so every word is spoken. A slide, which is no word without its
    /// pitch, is taken out, and the word before it held to the slide's end.
    /// </summary>
    public LyricDocument WithoutPitches()
    {
        if (!HasPitches)
            return this;
        return new([.. Lines.Select(static l => l with { Words = WordsWithoutPitches(l.Words) })]);
    }

    /// <summary>
    /// The document with each word's own times taken away, so the words of a line share its time by their letters, as
    /// they do for a format that gives only lines. A line keeps its end. Pitches go too, as a pitch is sung at a time.
    /// </summary>
    public LyricDocument WithoutWordTimes() =>
        new([.. WithoutPitches().Lines.Select(static l => l with { Words = [.. l.Words.Select(static w => new LyricWord(w.Text))] })]);

    /// <summary>The document with its lines in order of start, empty ones taken out.</summary>
    public static LyricDocument Of(IEnumerable<LyricLine> lines) =>
        new([.. lines.Where(static l => l.Words.Count > 0).OrderBy(static l => l.Start).ThenBy(static l => l.Singer)]);

    private static List<LyricWord> WordsWithoutPitches(IReadOnlyList<LyricWord> words)
    {
        var result = new List<LyricWord>(words.Count);
        foreach (var word in words)
        {
            if (word.IsSlide)
            {
                if (result.Count > 0 && (word.End ?? word.Start) is { } end && (result[^1].End is not { } before || before < end))
                    result[^1] = result[^1] with { End = end };
                continue;
            }

            result.Add(word with { Pitch = null });
        }

        return result;
    }
}
