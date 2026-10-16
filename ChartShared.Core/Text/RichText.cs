using System.Globalization;
using System.Text;

namespace ChartShared.Core.Text;

/// <summary>The formatting in effect for one <see cref="RichTextSpan"/>.</summary>
/// <param name="Bold">Inside <c>&lt;b&gt;</c>.</param>
/// <param name="Italic">Inside <c>&lt;i&gt;</c>.</param>
/// <param name="Underline">Inside <c>&lt;u&gt;</c>.</param>
/// <param name="Strikethrough">Inside <c>&lt;s&gt;</c>.</param>
/// <param name="Color">The colour from the innermost <c>&lt;color&gt;</c> as <c>0xAARRGGBB</c>, or <see langword="null"/> for the default.</param>
public readonly record struct RichTextStyle(bool Bold, bool Italic, bool Underline, bool Strikethrough, uint? Color);

/// <summary>A run of text with one style. A <c>&lt;br&gt;</c> becomes a span whose text is <c>"\n"</c>.</summary>
public sealed record RichTextSpan(string Text, RichTextStyle Style);

/// <summary>
/// Parses the rich-text tags that Clone Hero and YARG render in song metadata, such as
/// <c>&lt;color=#FDFD96FF&gt;Name&lt;/color&gt;</c> in <c>charter</c> or <c>loading_phrase</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both games display metadata through Unity's TextMeshPro, so this follows its rich-text rules
/// (https://docs.unity3d.com/Packages/com.unity.textmeshpro@4.0/manual/RichText.html):
/// </para>
/// <list type="bullet">
/// <item><c>b</c>, <c>i</c>, <c>u</c>, <c>s</c>, <c>color</c> and <c>br</c> are rendered.
/// <c>color</c> takes <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c> or one of
/// TextMeshPro's colour names, optionally in quotes (a lone leading or trailing quote is tolerated).</item>
/// <item>Closing tags end the most recent matching opening tag. An unclosed tag runs to the end.</item>
/// <item>Other TextMeshPro tags (<c>size</c>, <c>font</c>, <c>sup</c>, …) are removed but not rendered.</item>
/// <item>Anything else in angle brackets, like <c>&lt;3</c> or <c>&lt;unknown&gt;</c>, is ordinary text,
/// as is everything between <c>&lt;noparse&gt;</c> and <c>&lt;/noparse&gt;</c>.</item>
/// </list>
/// </remarks>
public static class RichText
{
    // Tags TextMeshPro understands that don't change what a single line of text in a table
    // looks like closely enough to be worth rendering. They're dropped rather than shown.
    private static readonly HashSet<string> StrippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "align", "allcaps", "alpha", "cspace", "font", "font-weight", "gradient", "indent",
        "line-height", "line-indent", "link", "lowercase", "margin", "margin-left", "margin-right",
        "mark", "mspace", "nobr", "page", "pos", "rotate", "size", "smallcaps", "space", "sprite",
        "style", "sub", "sup", "uppercase", "voffset", "width",
    };

    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0xFF000000,
        ["blue"] = 0xFF0000FF,
        ["green"] = 0xFF00FF00,
        ["orange"] = 0xFFFF8000,
        ["purple"] = 0xFFA020F0,
        ["red"] = 0xFFFF0000,
        ["white"] = 0xFFFFFFFF,
        ["yellow"] = 0xFFFFFF00,
    };

    /// <summary>Splits <paramref name="text"/> into styled spans with the tags removed.</summary>
    public static IReadOnlyList<RichTextSpan> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = new List<RichTextSpan>();
        var pending = new StringBuilder();
        var colors = new List<uint>();
        int bold = 0, italic = 0, underline = 0, strikethrough = 0;
        bool noParse = false;

        RichTextStyle Current() => new(bold > 0, italic > 0, underline > 0, strikethrough > 0, colors.Count > 0 ? colors[^1] : null);

        void Flush()
        {
            if (pending.Length == 0)
                return;

            // A tag that didn't change anything (e.g. a stray </b>) shouldn't split the text.
            RichTextStyle style = Current();
            if (spans.Count > 0 && spans[^1].Style == style && spans[^1].Text != "\n")
                spans[^1] = spans[^1] with { Text = spans[^1].Text + pending };
            else
                spans.Add(new RichTextSpan(pending.ToString(), style));
            pending.Clear();
        }

        int pos = 0;
        while (pos < text.Length)
        {
            int open = text.IndexOf('<', pos);
            if (open < 0)
            {
                pending.Append(text, pos, text.Length - pos);
                break;
            }

            pending.Append(text, pos, open - pos);
            int close = text.IndexOf('>', open + 1);
            if (close < 0)
            {
                pending.Append(text, open, text.Length - open);
                break;
            }

            ReadOnlySpan<char> tag = text.AsSpan(open + 1, close - open - 1);
            pos = close + 1;

            if (noParse)
            {
                if (tag.Equals("/noparse", StringComparison.OrdinalIgnoreCase))
                    noParse = false;
                else
                    pending.Append(text, open, close - open + 1);
                continue;
            }

            bool closing = tag.StartsWith("/");
            if (closing)
                tag = tag[1..];

            int equals = tag.IndexOf('=');
            // Names aren't trimmed: "a < b > c" is text, not a bold tag.
            ReadOnlySpan<char> name = equals < 0 ? tag : tag[..equals];
            ReadOnlySpan<char> value = equals < 0 ? default : tag[(equals + 1)..].Trim();

            // Every recognised tag changes the style from here on, so text so far is flushed first.
            switch (name.ToString().ToLowerInvariant())
            {
                case "b" when equals < 0:
                    Flush();
                    Adjust(ref bold, closing);
                    break;
                case "i" when equals < 0:
                    Flush();
                    Adjust(ref italic, closing);
                    break;
                case "u" when equals < 0:
                    Flush();
                    Adjust(ref underline, closing);
                    break;
                case "s" when equals < 0:
                    Flush();
                    Adjust(ref strikethrough, closing);
                    break;
                case "color" when closing && equals < 0:
                    Flush();
                    if (colors.Count > 0)
                        colors.RemoveAt(colors.Count - 1);
                    break;
                case "color" when !closing && TryParseColor(value, out uint color):
                    Flush();
                    colors.Add(color);
                    break;
                case "br" when !closing && equals < 0:
                    Flush();
                    spans.Add(new RichTextSpan("\n", Current()));
                    break;
                case "noparse" when !closing && equals < 0:
                    noParse = true;
                    break;
                case var other when StrippedTags.Contains(other):
                    break;
                default:
                    // Not a tag TextMeshPro knows, so it's shown as typed.
                    pending.Append(text, open, close - open + 1);
                    break;
            }
        }

        Flush();
        return spans;
    }

    /// <summary>The text with every recognised tag removed.</summary>
    public static string ToPlainText(string text) => string.Concat(Parse(text).Select(s => s.Text));

    /// <summary>True when <paramref name="text"/> contains any tag that <see cref="Parse"/> acts on.</summary>
    public static bool HasMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Every recognised tag removes characters, so the plain text is shorter exactly when there is markup.
        return text.Contains('<') && ToPlainText(text) != text;
    }

    private static void Adjust(ref int depth, bool closing) => depth = closing ? Math.Max(0, depth - 1) : depth + 1;

    private static bool TryParseColor(ReadOnlySpan<char> value, out uint argb)
    {
        argb = 0;
        // Quotes are optional, and charts in the wild often have only one, as in <color=#FF0000">.
        value = value.Trim('"');

        if (value.IsEmpty)
            return false;

        if (value[0] != '#')
            return NamedColors.TryGetValue(value.ToString(), out argb);

        ReadOnlySpan<char> hex = value[1..];
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint raw))
            return false;

        switch (hex.Length)
        {
            case 3: // RGB
                argb = 0xFF000000 | Expand(raw >> 8) << 16 | Expand(raw >> 4) << 8 | Expand(raw);
                return true;
            case 4: // RGBA
                argb = Expand(raw) << 24 | Expand(raw >> 12) << 16 | Expand(raw >> 8) << 8 | Expand(raw >> 4);
                return true;
            case 6: // RRGGBB
                argb = 0xFF000000 | raw;
                return true;
            case 8: // RRGGBBAA
                argb = raw << 24 | raw >> 8;
                return true;
            default:
                return false;
        }

        // One hex digit, repeated to fill a byte: F becomes FF.
        static uint Expand(uint nibble) => (nibble & 0xF) * 0x11;
    }
}
