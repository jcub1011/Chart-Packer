using System.Globalization;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the lyrics of an UltraStar <c>.txt</c> song file. Written from the format's published description (see
/// THIRD-PARTY-NOTICES), not from any implementation. Pitches are not read: the words are timed, and the import makes them spoken.
/// </summary>
/// <remarks>
/// <para>A header of <c>#KEY:value</c> lines gives <c>BPM</c>, the beats a minute counted in quarters, and <c>GAP</c>, the
/// milliseconds before beat 0. A note line is its kind (<c>:</c> normal, <c>*</c> golden, <c>F</c> freestyle, <c>R</c> and
/// <c>G</c> rap), its start beat, its length in beats, its pitch and its syllable; a <c>-</c> line ends a line of lyrics.
/// A beat is <c>15 / BPM</c> seconds.</para>
/// <para>A syllable that ends in a space, or one the next begins with a space, ends a word; otherwise the next carries it
/// on, so the first ends in a hyphen. A syllable of <c>~</c> only lengthens the one before it. Of a duet, the first
/// singer's lines are read.</para>
/// </remarks>
public static class UltraStarParser
{
    /// <summary>Whether <paramref name="text"/> looks like an UltraStar song: it has a <c>#BPM</c> header.</summary>
    public static bool LooksLikeUltraStar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart('﻿').StartsWith("#BPM:", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Reads <paramref name="text"/> as an UltraStar song.</summary>
    /// <exception cref="ChartFormatException">It has no usable <c>#BPM</c>.</exception>
    public static LyricDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double bpm = 0, gapMs = 0;
        var lines = new List<LyricLine>();
        var words = new List<LyricWord>();
        TimeSpan? lineStart = null, lineEnd = null;
        bool joinsNext = false;
        int player = 1;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', '\n').TrimStart('﻿');
            if (line.Length == 0)
                continue;

            if (line[0] == '#')
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    string key = line[1..colon].Trim();
                    string value = line[(colon + 1)..].Trim().Replace(',', '.');
                    if (key.Equals("BPM", StringComparison.OrdinalIgnoreCase))
                        _ = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out bpm);
                    else if (key.Equals("GAP", StringComparison.OrdinalIgnoreCase))
                        _ = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out gapMs);
                }

                continue;
            }

            char kind = line[0];
            if (kind == 'E')
                break;
            if (kind == 'P' && line.Length > 1 && int.TryParse(line.AsSpan(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int who))
            {
                EndLine();
                player = who;
                continue;
            }

            if (kind == '-')
            {
                EndLine();
                continue;
            }

            if (kind is not (':' or '*' or 'F' or 'R' or 'G') || player > 1)
                continue;

            // kind, start, length, pitch, then the syllable as it is, spaces and all.
            string[] parts = line.Split(' ', 5);
            if (parts.Length < 4 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int startBeat)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int lengthBeats))
            {
                continue;
            }

            if (bpm <= 0)
                throw new ChartFormatException("The UltraStar file has no #BPM, so its beats can't be timed.");
            string syllable = parts.Length > 4 ? parts[4] : string.Empty;
            var start = Time(startBeat);
            var end = Time(startBeat + lengthBeats);
            lineStart ??= start;
            lineEnd = end;

            string word = syllable.Trim();
            if (word == "~")
            {
                if (words.Count > 0)
                    words[^1] = words[^1] with { End = end };
                continue;
            }

            if (word.Length == 0)
                continue;
            if (joinsNext && !char.IsWhiteSpace(syllable[0]) && words.Count > 0 && !words[^1].Text.EndsWith('-'))
                words[^1] = words[^1] with { Text = words[^1].Text + "-" };
            words.Add(new LyricWord(word, start, end));
            joinsNext = !char.IsWhiteSpace(syllable[^1]);
        }

        EndLine();
        return LyricDocument.Of(lines);

        TimeSpan Time(int beat) => TimeSpan.FromSeconds((gapMs / 1000) + (beat * 15 / bpm));

        void EndLine()
        {
            if (words.Count > 0 && lineStart is { } start)
                lines.Add(new LyricLine(start, lineEnd, [.. words]));
            words.Clear();
            lineStart = lineEnd = null;
            joinsNext = false;
        }
    }

    /// <summary>Reads the UltraStar song in <paramref name="bytes"/>, in whatever encoding it's in.</summary>
    public static LyricDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(ChartTextReader.Decode(bytes));
    }
}
