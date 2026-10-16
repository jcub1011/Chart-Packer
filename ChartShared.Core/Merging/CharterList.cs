using System.Text;
using ChartShared.Core.Text;

namespace ChartShared.Core.Merging;

/// <summary>Combines the <c>charter</c> values of several charts into one list.</summary>
public static class CharterList
{
    /// <summary>What joins the names of a merged list.</summary>
    public const string Separator = ", ";

    private static readonly char[] Separators = [',', '&', '/', '+'];

    /// <summary>
    /// Every name in <paramref name="charters"/>, in order, each once: a value is split into names at
    /// commas, ampersands, slashes and plus signs outside its rich-text tags, and names are the same
    /// when their text without tags is, ignoring case. The first spelling of a name is kept, tags and
    /// all; a name whose tags span a separator isn't split.
    /// </summary>
    public static string Merge(IEnumerable<string?> charters)
    {
        ArgumentNullException.ThrowIfNull(charters);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? charter in charters)
        {
            if (string.IsNullOrWhiteSpace(charter))
                continue;

            foreach (string name in Split(charter))
            {
                string plain = RichText.ToPlainText(name).Trim();
                if (plain.Length > 0 && seen.Add(plain))
                    names.Add(name);
            }
        }

        return string.Join(Separator, names);
    }

    /// <summary>The names in <paramref name="charter"/>, trimmed.</summary>
    internal static IEnumerable<string> Split(string charter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < charter.Length; i++)
        {
            char c = charter[i];
            if (c == '<' && TagAt(charter, i) is { } tag)
            {
                depth = Math.Max(0, depth + (tag.Closing ? -1 : tag.SelfClosing ? 0 : 1));
                current.Append(charter, i, tag.Length);
                i += tag.Length - 1;
                continue;
            }

            if (depth == 0 && Separators.Contains(c))
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0);
    }

    /// <summary>The tag starting at <paramref name="i"/>: <c>&lt;name…&gt;</c> or <c>&lt;/name&gt;</c>, if there's one.</summary>
    private static (int Length, bool Closing, bool SelfClosing)? TagAt(string text, int i)
    {
        int end = text.IndexOf('>', i + 1);
        if (end < 0)
            return null;

        string inner = text[(i + 1)..end];
        bool closing = inner.StartsWith('/');
        string name = (closing ? inner[1..] : inner).Split('=', ' ')[0].Trim();
        if (name.Length == 0 || !name.All(ch => char.IsLetter(ch) || ch == '-'))
            return null;

        // A tag that's never closed (br, sprite, …) doesn't open anything.
        bool selfClosing = !closing && text.IndexOf("</" + name, end, StringComparison.OrdinalIgnoreCase) < 0;
        return (end - i + 1, closing, selfClosing);
    }
}
