using System.Globalization;
using System.Text.RegularExpressions;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads LRC lyrics, with or without the A2 enhanced extension that times each word. Written from the format's published
/// description (see THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// <para>A line is one or more <c>[mm:ss.xx]</c> time tags and its text; the line ends where the next starts. An empty line
/// with a time tag only marks the end of the one before it. Tags such as <c>[ti:]</c> and <c>[ar:]</c> are skipped, but
/// <c>[offset:ms]</c> is kept to: a positive offset brings the lyrics forward.</para>
/// <para>A2 puts <c>&lt;mm:ss.xx&gt;</c> before each word. A text segment that follows another with no space between is
/// the rest of a word, so the word is made a syllable joined to it, as <c>Hel-</c> <c>lo</c>.</para>
/// </remarks>
public static partial class LrcParser
{
    /// <summary>Reads <paramref name="text"/> as LRC.</summary>
    /// <param name="useWordTimes">Whether A2's word times are used; when false they're taken out and each line's words share its time.</param>
    public static LyricDocument Parse(string text, bool useWordTimes = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<LyricLine>();
        var ends = new List<TimeSpan>();
        double offsetMs = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] != '[')
                continue;

            var starts = new List<TimeSpan>();
            int at = 0;
            while (at < line.Length && line[at] == '[')
            {
                var match = TimeTag().Match(line, at);
                if (match.Success && match.Index == at)
                {
                    starts.Add(TimeOf(match));
                    at += match.Length;
                    continue;
                }

                // Not a time: metadata, which is the whole of the line.
                int close = line.IndexOf(']', at);
                if (starts.Count == 0 && close > 0)
                {
                    string tag = line[(at + 1)..close];
                    if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(tag.AsSpan(7).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
                    {
                        offsetMs = ms;
                    }
                }

                break;
            }

            if (starts.Count == 0)
                continue;

            var (words, lineEnd) = Words(line[at..], useWordTimes);
            foreach (var start in starts)
            {
                if (words.Count == 0)
                    ends.Add(start);
                else
                    lines.Add(new LyricLine(start, lineEnd, words));
            }
        }

        var shift = TimeSpan.FromMilliseconds(-offsetMs);
        var ordered = lines.OrderBy(static l => l.Start).ToList();
        var result = new List<LyricLine>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1].Start : TimeSpan.MaxValue;
            var end = line.End;
            if (end is null)
            {
                // The earliest empty line between this one and the next ends it.
                var marker = ends.Where(e => e > line.Start && e <= next).Select(static e => (TimeSpan?)e).Min();
                end = marker;
            }

            result.Add(Shifted(line with { End = end }, shift));
        }

        return LyricDocument.Of(result);
    }

    private static LyricLine Shifted(LyricLine line, TimeSpan shift)
    {
        if (shift == TimeSpan.Zero)
            return line;
        return new LyricLine(
            Max0(line.Start + shift),
            line.End is { } end ? Max0(end + shift) : null,
            [.. line.Words.Select(w => w with { Start = w.Start is { } s ? Max0(s + shift) : null, End = w.End is { } e ? Max0(e + shift) : null })]);
    }

    private static TimeSpan Max0(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;

    private static TimeSpan TimeOf(Match match) =>
        TimeSpan.FromMinutes(int.Parse(match.Groups["m"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture))
        + TimeSpan.FromSeconds(int.Parse(match.Groups["s"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture))
        + LyricTime.Fraction(match.Groups["f"].ValueSpan);

    // The words of the text after a line's time tags, and the end an A2 tag after its last word says.
    private static (List<LyricWord> Words, TimeSpan? End) Words(string text, bool useWordTimes)
    {
        var words = new List<LyricWord>();
        TimeSpan? lineEnd = null;
        TimeSpan? at = null;
        bool joinsPrevious = false;
        int position = 0;

        var tags = WordTag().Matches(text);
        foreach (Match tag in tags)
        {
            Segment(text[position..tag.Index]);
            position = tag.Index + tag.Length;
            if (useWordTimes)
            {
                at = TimeOf(tag);
                if (words.Count > 0 && words[^1].End is null)
                    words[^1] = words[^1] with { End = at };
            }
        }

        string rest = text[position..];
        Segment(rest);

        // A tag after the last word is its end, not another word's start.
        if (useWordTimes && words.Count > 0 && tags.Count > 0 && rest.Trim().Length == 0)
            lineEnd = at;

        return (words, lineEnd);

        void Segment(string segment)
        {
            if (segment.Length == 0)
                return;
            var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                joinsPrevious = false;
                return;
            }

            bool startsWithSpace = char.IsWhiteSpace(segment[0]);
            for (int i = 0; i < tokens.Length; i++)
            {
                // A first token not set off by a space carries on the word before it.
                if (i == 0 && !startsWithSpace && joinsPrevious && words.Count > 0)
                {
                    var before = words[^1];
                    if (!before.Text.EndsWith('-'))
                        words[^1] = before with { Text = before.Text + "-" };
                }

                words.Add(new LyricWord(tokens[i], i == 0 ? at : null));
            }

            joinsPrevious = !char.IsWhiteSpace(segment[^1]);
        }
    }

    [GeneratedRegex(@"\[(?<m>\d{1,4}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"<(?<m>\d{1,4}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?>")]
    private static partial Regex WordTag();

    /// <summary>Whether <paramref name="text"/> looks like LRC: a line starts with a time tag.</summary>
    public static bool LooksLikeLrc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string line in text.Split('\n'))
        {
            if (TimeTag().Match(line.TrimStart()) is { Success: true, Index: 0 })
                return true;
        }

        return false;
    }

    /// <summary>Reads the LRC in <paramref name="bytes"/>, in whatever encoding it's in.</summary>
    public static LyricDocument Parse(byte[] bytes, bool useWordTimes = true)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(Charts.ChartTextReader.Decode(bytes), useWordTimes);
    }
}
