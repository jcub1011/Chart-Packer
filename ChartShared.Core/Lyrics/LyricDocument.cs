namespace ChartShared.Core.Lyrics;

/// <summary>A word, or syllable, of a lyric line.</summary>
/// <param name="Text">The text, with no spaces around it.</param>
/// <param name="Start">When it starts in the audio, or null when the format gives no time for it, so it's worked out from its line.</param>
/// <param name="End">When it ends, or null when the format doesn't say.</param>
public sealed record LyricWord(string Text, TimeSpan? Start = null, TimeSpan? End = null);

/// <summary>A line of lyrics: a phrase.</summary>
/// <param name="Start">When the line starts in the audio.</param>
/// <param name="End">When the line ends, or null when the format doesn't say; the next line's start is then taken.</param>
/// <param name="Words">The line's words, in order.</param>
public sealed record LyricLine(TimeSpan Start, TimeSpan? End, IReadOnlyList<LyricWord> Words)
{
    /// <summary>A line of <paramref name="text"/>, split into words at its spaces.</summary>
    public static LyricLine OfText(TimeSpan start, TimeSpan? end, string text) =>
        new(start, end, [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(static w => new LyricWord(w))]);
}

/// <summary>
/// Lyrics read from a file, whatever it was: lines of words with the times the format gave, in the audio's time, before
/// any song delay. A word with no time of its own is given one by <see cref="Charting.Editing.LyricImport"/>.
/// </summary>
/// <param name="Lines">The lines, by start time.</param>
public sealed record LyricDocument(IReadOnlyList<LyricLine> Lines)
{
    /// <summary>How many words the document has.</summary>
    public int WordCount => Lines.Sum(static l => l.Words.Count);

    /// <summary>Whether any word has a time of its own, which <see cref="WithoutWordTimes"/> takes away.</summary>
    public bool HasWordTimes => Lines.Any(static l => l.Words.Any(static w => w.Start is not null));

    /// <summary>
    /// The document with each word's own times taken away, so the words of a line share its time by their letters, as
    /// they do for a format that gives only lines. A line keeps its end.
    /// </summary>
    public LyricDocument WithoutWordTimes() =>
        new([.. Lines.Select(static l => l with { Words = [.. l.Words.Select(static w => new LyricWord(w.Text))] })]);

    /// <summary>The document with its lines in order of start, empty ones taken out.</summary>
    public static LyricDocument Of(IEnumerable<LyricLine> lines) =>
        new([.. lines.Where(static l => l.Words.Count > 0).OrderBy(static l => l.Start)]);
}
