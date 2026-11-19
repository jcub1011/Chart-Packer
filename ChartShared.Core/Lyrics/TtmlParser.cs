using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Lyrics;

/// <summary>
/// Reads the paragraphs of a TTML (Timed Text Markup Language) file as lines of lyrics, and the spans inside them as words
/// when they're timed. Written from the W3C's published description (see THIRD-PARTY-NOTICES), not from any implementation.
/// </summary>
/// <remarks>
/// Times are clock times (<c>hh:mm:ss.fff</c>, frames after another colon) or offsets (<c>1.5s</c>, <c>500ms</c>, <c>2m</c>,
/// <c>1h</c>, <c>30f</c>, <c>45t</c>). A paragraph's times are relative to the <c>body</c> and <c>div</c> around it, and a
/// span's to its paragraph. Spans with nothing between them are the parts of one word, so each but the last ends in a hyphen.
/// </remarks>
public static partial class TtmlParser
{
    /// <summary>Reads <paramref name="text"/> as TTML.</summary>
    /// <exception cref="ChartFormatException">It isn't well-formed XML.</exception>
    public static LyricDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        XDocument document;
        try
        {
            // No DTD and no external references: lyrics are not a place to resolve them.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new ChartFormatException("The lyrics aren't well-formed TTML: " + e.Message);
        }

        var root = document.Root ?? throw new ChartFormatException("The lyrics have no TTML content.");
        var rates = RatesOf(root);
        var lines = new List<LyricLine>();
        foreach (var p in root.Descendants().Where(static e => e.Name.LocalName == "p"))
        {
            var origin = TimeSpan.Zero;
            foreach (var parent in p.Ancestors().Reverse())
            {
                if (TimeOf(parent, "begin", rates) is { } begin)
                    origin += begin;
            }

            var (start, end) = Span(p, origin, rates);
            if (start is not { } from)
                continue;

            var words = Words(p, from, end, rates);
            if (words.Count > 0)
                lines.Add(new LyricLine(from, end, words));
        }

        return LyricDocument.Of(lines);
    }

    /// <summary>Reads the TTML in <paramref name="bytes"/>.</summary>
    public static LyricDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(ChartTextReader.Decode(bytes));
    }

    // The start and end of an element, from its begin and end or dur, inside a container that starts at origin.
    private static (TimeSpan? Start, TimeSpan? End) Span(XElement element, TimeSpan origin, Rates rates)
    {
        var begin = TimeOf(element, "begin", rates);
        var end = TimeOf(element, "end", rates);
        var dur = TimeOf(element, "dur", rates);
        TimeSpan? start = begin is { } b ? origin + b : null;
        TimeSpan? stop = end is { } e ? origin + e : (start is { } s && dur is { } d ? s + d : null);
        return (start, stop);
    }

    // A span's start and end. They're relative to the paragraph, but some files, Apple's among them, give them in the song's
    // time: a span that'd start after its paragraph ends if relative, and starts inside it if not, is taken as the latter.
    private static (TimeSpan? Start, TimeSpan? End) SpanIn(XElement span, TimeSpan origin, TimeSpan? paragraphEnd, Rates rates)
    {
        var slack = TimeSpan.FromMilliseconds(50);
        var begin = TimeOf(span, "begin", rates);
        if (begin is { } b && paragraphEnd is { } stop && origin + b > stop + slack && b <= stop + slack && b >= origin - slack)
            return Span(span, TimeSpan.Zero, rates);
        return Span(span, origin, rates);
    }

    private static List<LyricWord> Words(XElement p, TimeSpan origin, TimeSpan? paragraphEnd, Rates rates)
    {
        var words = new List<LyricWord>();
        bool space = true;

        void Text(string text, TimeSpan? start, TimeSpan? end)
        {
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                space |= text.Length > 0;
                return;
            }

            if (char.IsWhiteSpace(text[0]))
                space = true;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!space && words.Count > 0 && !words[^1].Text.EndsWith('-'))
                    words[^1] = words[^1] with { Text = words[^1].Text + "-" };
                words.Add(new LyricWord(tokens[i], i == 0 ? start : null, tokens.Length == 1 ? end : null));
                space = i < tokens.Length - 1 || char.IsWhiteSpace(text[^1]);
            }
        }

        void Walk(XElement element)
        {
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText t:
                        Text(t.Value, null, null);
                        break;
                    case XElement { Name.LocalName: "br" }:
                        space = true;
                        break;
                    case XElement e:
                        var (start, end) = SpanIn(e, origin, paragraphEnd, rates);
                        if (start is not null && !e.Elements().Any())
                            Text(e.Value, start, end);
                        else
                            Walk(e);
                        break;
                }
            }
        }

        Walk(p);
        return words;
    }

    private readonly record struct Rates(double Frames, double Ticks);

    private static Rates RatesOf(XElement root)
    {
        double frames = Number(root, "frameRate") ?? 30;
        double ticks = Number(root, "tickRate") ?? (root.Attributes().Any(static a => a.Name.LocalName == "frameRate") ? frames * (Number(root, "subFrameRate") ?? 1) : 1);
        return new Rates(frames, ticks);
    }

    private static double? Number(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name) is { } a
            && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0 ? value : null;

    private static TimeSpan? TimeOf(XElement element, string name, Rates rates)
    {
        var attribute = element.Attributes().FirstOrDefault(a => a.Name.LocalName == name && a.Name.NamespaceName.Length == 0);
        return attribute is null ? null : Expression(attribute.Value.Trim(), rates);
    }

    private static TimeSpan? Expression(string text, Rates rates)
    {
        if (Offset().Match(text) is { Success: true } offset)
        {
            double value = double.Parse(offset.Groups["v"].Value, CultureInfo.InvariantCulture);
            double seconds = offset.Groups["u"].Value switch
            {
                "h" => value * 3600,
                "m" => value * 60,
                "s" => value,
                "ms" => value / 1000,
                "f" => value / rates.Frames,
                _ => value / rates.Ticks,
            };
            return TimeSpan.FromSeconds(seconds);
        }

        if (Clock().Match(text) is { Success: true } clock)
        {
            double seconds = (int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600)
                + (int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) * 60)
                + int.Parse(clock.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (clock.Groups["f"].Success)
                seconds += LyricTime.Fraction(clock.Groups["f"].ValueSpan).TotalSeconds;
            else if (clock.Groups["fr"].Success)
                seconds += int.Parse(clock.Groups["fr"].Value, CultureInfo.InvariantCulture) / rates.Frames;
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    [GeneratedRegex(@"^(?<v>\d+(?:\.\d+)?)(?<u>h|ms|m|s|f|t)$")]
    private static partial Regex Offset();

    [GeneratedRegex(@"^(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})(?:\.(?<f>\d{1,9})|:(?<fr>\d{1,3}))?$")]
    private static partial Regex Clock();
}
