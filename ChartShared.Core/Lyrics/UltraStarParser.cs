using System.Globalization;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the lyrics of an UltraStar <c>.txt</c> song file, with their pitches. Written from the format's published
/// description (see THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// <para>A header of <c>#KEY:value</c> lines gives <c>BPM</c>, the beats a minute counted in quarters, and <c>GAP</c>, the
/// milliseconds before beat 0. A note line is its kind (<c>:</c> normal, <c>*</c> golden, <c>F</c> freestyle, <c>R</c> and
/// <c>G</c> rap), its start beat, its length in beats, its pitch and its syllable; a <c>-</c> line ends a line of lyrics.
/// A beat is <c>15 / BPM</c> seconds.</para>
/// <para>A normal or golden note's pitch is in semitones from C4, MIDI note 60, so a word's <see cref="LyricWord.Pitch"/>
/// is 60 more. Freestyle and rap notes aren't sung to a pitch, so their words have none.</para>
/// <para>A syllable that ends in a space, or one the next begins with a space, ends a word; otherwise the next carries it
/// on, so the first ends in a hyphen. A syllable of <c>~</c> carries the one before it on: at the same pitch it only
/// lengthens it, and at another it's a slide (<see cref="LyricWord.IsSlide"/>).</para>
/// <para>A duet's <c>P1</c> and <c>P2</c> sections are singers 1 and 2 (<see cref="LyricLine.Singer"/>); a <c>P3</c>
/// section, which older duets have both sing, is read as both's.</para>
/// </remarks>
public static class UltraStarParser
{
    /// <summary>The MIDI pitch an UltraStar pitch of 0 is: C4.</summary>
    public const int PitchZero = 60;

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
        int[] singers = [1];

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
                singers = who == 3 ? [1, 2] : [Math.Max(1, who)];
                continue;
            }

            if (kind == '-')
            {
                EndLine();
                continue;
            }

            if (kind is not (':' or '*' or 'F' or 'R' or 'G'))
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
            int? pitch = kind is ':' or '*' && int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tone)
                ? PitchZero + tone
                : null;
            lineStart ??= start;
            lineEnd = end;

            string word = syllable.Trim();
            if (word == "~")
            {
                if (words.Count == 0)
                    continue;

                // At another pitch it's a slide; otherwise it only holds the syllable before on.
                var before = words[^1];
                if (pitch is not null && before.Pitch is not null && pitch != before.Pitch)
                    words.Add(new LyricWord(LyricWord.SlideText, start, end, pitch));
                else
                    words[^1] = before with { End = end };
                continue;
            }

            if (word.Length == 0)
                continue;
            int last = words.FindLastIndex(static w => !w.IsSlide);
            if (joinsNext && !char.IsWhiteSpace(syllable[0]) && last >= 0 && !words[last].Text.EndsWith('-'))
                words[last] = words[last] with { Text = words[last].Text + "-" };
            words.Add(new LyricWord(word, start, end, pitch));
            joinsNext = !char.IsWhiteSpace(syllable[^1]);
        }

        EndLine();
        return LyricDocument.Of(lines);

        TimeSpan Time(int beat) => TimeSpan.FromSeconds((gapMs / 1000) + (beat * 15 / bpm));

        void EndLine()
        {
            if (words.Count > 0 && lineStart is { } start)
            {
                foreach (int singer in singers)
                    lines.Add(new LyricLine(start, lineEnd, [.. words], singer));
            }

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
