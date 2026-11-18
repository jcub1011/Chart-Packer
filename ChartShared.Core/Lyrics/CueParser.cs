using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the cues of an SRT or a WebVTT file as lines of lyrics. Written from the formats' published descriptions (see
/// THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// A cue is a line <c>start --&gt; end</c> and the text under it, up to a blank line; what comes before it, such as a number
/// or an identifier, and WebVTT's header, <c>NOTE</c>, <c>STYLE</c> and <c>REGION</c> blocks, is skipped. The text's tags
/// (<c>&lt;i&gt;</c>, <c>&lt;c.yellow&gt;</c>, <c>&lt;00:01.000&gt;</c>, SRT's <c>{\an8}</c>) are taken out, its entities read, and a
/// cue's lines made one line of lyrics. A cue has no word times, so its words share its time.
/// </remarks>
public static partial class CueParser
{
    /// <summary>Reads <paramref name="text"/> as SRT or WebVTT.</summary>
    public static LyricDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<LyricLine>();
        var cueText = new StringBuilder();
        TimeSpan? start = null, end = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                Flush();
                var left = line.AsSpan(0, arrow);
                var right = line.AsSpan(arrow + 3).Trim();

                // WebVTT's cue settings follow the end time.
                int space = right.IndexOfAny(' ', '\t');
                if (space >= 0)
                    right = right[..space];
                if (LyricTime.TryParseClock(left, out var from) && LyricTime.TryParseClock(right, out var to))
                    (start, end) = (from, to);
                continue;
            }

            if (start is not null)
                cueText.Append(cueText.Length > 0 ? " " : "").Append(line);
        }

        Flush();
        return LyricDocument.Of(lines);

        void Flush()
        {
            if (start is { } from)
            {
                string plain = Clean(cueText.ToString());
                if (plain.Length > 0)
                    lines.Add(LyricLine.OfText(from, end, plain));
            }

            start = end = null;
            cueText.Clear();
        }
    }

    /// <summary>Reads the SRT or WebVTT in <paramref name="bytes"/>, in whatever encoding it's in.</summary>
    public static LyricDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(Charts.ChartTextReader.Decode(bytes));
    }

    // The text with its tags taken out and its entities read.
    private static string Clean(string text)
    {
        string plain = WebUtility.HtmlDecode(Tags().Replace(text, string.Empty));
        return Spaces().Replace(plain.Replace('♪', ' ').Replace('♫', ' '), " ").Trim();
    }

    [GeneratedRegex(@"<[^>]*>|\{[^}]*\}")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
