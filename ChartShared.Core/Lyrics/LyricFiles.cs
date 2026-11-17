using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>The kinds of file lyrics can be read from.</summary>
public enum LyricFormat
{
    /// <summary>LRC, with or without A2 word times.</summary>
    Lrc,

    /// <summary>SubRip.</summary>
    Srt,

    /// <summary>WebVTT.</summary>
    Vtt,

    /// <summary>Timed Text Markup Language.</summary>
    Ttml,

    /// <summary>A MIDI karaoke file.</summary>
    Kar,

    /// <summary>An UltraStar song file.</summary>
    UltraStar,

    /// <summary>The tags of an audio file.</summary>
    AudioTags,
}

/// <summary>Reads lyrics from a file of any of the <see cref="LyricFormat"/>s, by its name.</summary>
public static class LyricFiles
{
    /// <summary>The extensions, with their dot, the lyrics files are picked by.</summary>
    public static IReadOnlyList<string> TextExtensions { get; } = [".lrc", ".srt", ".vtt", ".ttml", ".dfxp", ".xml", ".kar", ".txt"];

    /// <summary>The extensions, with their dot, of the audio files whose tags are read.</summary>
    public static IReadOnlyList<string> AudioExtensions { get; } = [".mp3", ".ogg", ".opus", ".oga", ".flac", ".m4a", ".wav"];

    /// <summary>The format <paramref name="path"/> is read as, or null when its extension isn't one lyrics are read from.</summary>
    public static LyricFormat? FormatOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".lrc" => LyricFormat.Lrc,
            ".srt" => LyricFormat.Srt,
            ".vtt" => LyricFormat.Vtt,
            ".ttml" or ".dfxp" or ".xml" => LyricFormat.Ttml,
            ".kar" => LyricFormat.Kar,
            ".txt" => LyricFormat.UltraStar,
            _ when AudioExtensions.Contains(extension) => LyricFormat.AudioTags,
            _ => null,
        };
    }

    /// <summary>Reads the lyrics in the file at <paramref name="path"/>.</summary>
    /// <param name="useWordTimes">Whether the word times of A2 lyrics are used; when false they're ignored.</param>
    /// <exception cref="ChartFormatException">The file's extension isn't one lyrics are read from, or its content isn't what it should be.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static LyricDocument Read(string path, bool useWordTimes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var format = FormatOf(path) ?? throw new ChartFormatException("Lyrics can't be read from a " + Path.GetExtension(path) + " file.");
        if (format == LyricFormat.AudioTags)
            return Check(EmbeddedLyricsReader.Read(path, useWordTimes));

        byte[] bytes = File.ReadAllBytes(path);
        var document = format switch
        {
            LyricFormat.Lrc => LrcParser.Parse(bytes, useWordTimes),
            LyricFormat.Srt or LyricFormat.Vtt => CueParser.Parse(bytes),
            LyricFormat.Ttml => TtmlParser.Parse(bytes),
            LyricFormat.Kar => KarParser.Parse(bytes),
            _ => UltraStarText(bytes),
        };
        return Check(document);
    }

    private static LyricDocument UltraStarText(byte[] bytes)
    {
        string text = ChartTextReader.Decode(bytes);
        return UltraStarParser.LooksLikeUltraStar(text)
            ? UltraStarParser.Parse(text)
            : throw new ChartFormatException("The text file isn't an UltraStar song: it has no #BPM header.");
    }

    private static LyricDocument Check(LyricDocument document) =>
        document.WordCount > 0 ? document : throw new ChartFormatException("The file has no lyrics in it.");
}
