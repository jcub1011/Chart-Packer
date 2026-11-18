using ATL;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the lyrics an audio file has in its tags: an ID3v2 synchronized lyrics frame (<c>SYLT</c>) or unsynchronized one
/// (<c>USLT</c>) in an MP3, a <c>LYRICS</c> comment in a Vorbis file, and the like. The tags are read by
/// <see href="https://github.com/Zeugma440/atldotnet">ATL.NET</see> (MIT License; see <c>THIRD-PARTY-NOTICES.md</c>).
/// </summary>
/// <remarks>
/// <para>Timed lyrics are read as they're given. Entries with a line feed in their text are syllables, as in a karaoke
/// file, and a line feed ends the line; otherwise each entry is a line. Lyrics with no times, as plain text, can't be
/// placed on a song, so they're refused; text that is LRC is read as LRC.</para>
/// </remarks>
public static class EmbeddedLyricsReader
{
    private const int BufferSize = 64 * 1024;

    static EmbeddedLyricsReader()
    {
        // ATL writes the stack trace of every file it can't parse to the console by default.
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    /// <summary>Reads the lyrics in the tags of the audio file at <paramref name="path"/>.</summary>
    /// <param name="useWordTimes">Whether the word times of A2 lyrics are used, as <see cref="LrcParser.Parse(string, bool)"/> does.</param>
    /// <exception cref="ChartFormatException">The file can't be read, or has no timed lyrics.</exception>
    /// <exception cref="IOException">The file can't be opened.</exception>
    public static LyricDocument Read(string path, bool useWordTimes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        Track track;
        try
        {
            track = new Track(stream, Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ATL reports damaged or unrecognised audio as assorted exceptions.
            throw new ChartFormatException("The audio file can't be read.");
        }

        return Of(track.Lyrics, useWordTimes) ?? throw new ChartFormatException("The audio file's tags have no timed lyrics.");
    }

    /// <summary>The lyrics <paramref name="tags"/> hold, or null when none are timed.</summary>
    internal static LyricDocument? Of(IEnumerable<LyricsInfo>? tags, bool useWordTimes = true)
    {
        var candidates = (tags ?? []).Where(static l => l.ContentType is LyricsInfo.LyricsType.LYRICS or LyricsInfo.LyricsType.OTHER).ToList();

        foreach (var info in candidates)
        {
            if (info.SynchronizedLyrics is { Count: > 0 } synced && Synchronized(synced) is { Lines.Count: > 0 } document)
                return document;
        }

        foreach (var info in candidates)
        {
            if (info.UnsynchronizedLyrics is { Length: > 0 } text && LrcParser.LooksLikeLrc(text) && LrcParser.Parse(text, useWordTimes) is { Lines.Count: > 0 } document)
                return document;
        }

        return null;
    }

    private static LyricDocument Synchronized(IList<LyricsInfo.LyricsPhrase> phrases)
    {
        bool syllables = phrases.Any(static p => p.Text is { } t && (t.Contains('\n') || t.Contains('\r')));
        var lines = new List<LyricLine>();
        if (!syllables)
        {
            foreach (var phrase in phrases.OrderBy(static p => p.TimestampStart))
            {
                if (phrase.Text is { } text && text.Trim().Length > 0)
                    lines.Add(LyricLine.OfText(TimeSpan.FromMilliseconds(phrase.TimestampStart), End(phrase), text));
            }

            return LyricDocument.Of(lines);
        }

        var words = new List<LyricWord>();
        TimeSpan? start = null;
        bool joinsNext = false;
        foreach (var phrase in phrases.OrderBy(static p => p.TimestampStart))
        {
            string text = phrase.Text ?? string.Empty;
            bool breakBefore = text.TrimStart(' ').StartsWith('\n') || text.TrimStart(' ').StartsWith('\r');
            bool breakAfter = text.TrimEnd(' ').EndsWith('\n') || text.TrimEnd(' ').EndsWith('\r');
            text = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            if (breakBefore)
                EndLine();

            string word = text.Trim();
            if (word.Length > 0)
            {
                var at = TimeSpan.FromMilliseconds(phrase.TimestampStart);
                start ??= at;
                if (joinsNext && !char.IsWhiteSpace(text[0]) && words.Count > 0 && !words[^1].Text.EndsWith('-'))
                    words[^1] = words[^1] with { Text = words[^1].Text + "-" };
                words.Add(new LyricWord(word, at, End(phrase)));
                joinsNext = !char.IsWhiteSpace(text[^1]);
            }

            if (breakAfter)
                EndLine();
        }

        EndLine();
        return LyricDocument.Of(lines);

        void EndLine()
        {
            if (words.Count > 0 && start is { } at)
                lines.Add(new LyricLine(at, null, [.. words]));
            words.Clear();
            start = null;
            joinsNext = false;
        }
    }

    private static TimeSpan? End(LyricsInfo.LyricsPhrase phrase) =>
        phrase.TimestampEnd > phrase.TimestampStart ? TimeSpan.FromMilliseconds(phrase.TimestampEnd) : null;
}
