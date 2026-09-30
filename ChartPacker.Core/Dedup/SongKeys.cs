using System.Globalization;
using System.Text;

namespace ChartPacker.Core.Dedup;

/// <summary>Folds an artist and title into a key that different spellings of the same song share.</summary>
/// <remarks>
/// The folding is deliberately conservative: case, accents, spacing and typographic quotes and
/// dashes are ignored, but words such as <c>(Live)</c>, <c>feat.</c> or a leading <c>The</c> are
/// kept, because they often mark a different recording.
/// </remarks>
public static class SongKeys
{
    /// <summary>
    /// The key for a song, or <see langword="null"/> when <paramref name="artist"/> or
    /// <paramref name="title"/> is missing. Both should already be free of rich-text tags, as
    /// <see cref="Metadata.SongHeadline"/> gives them.
    /// </summary>
    public static string? For(string? artist, string? title)
    {
        string? a = Fold(artist);
        string? t = Fold(title);
        return a is null || t is null ? null : a + "\u001F" + t;
    }

    /// <summary>Folds one value, or returns <see langword="null"/> when nothing is left of it.</summary>
    public static string? Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = new StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(c))
            {
                space = text.Length > 0;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(FoldPunctuation(char.ToLowerInvariant(c)));
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private static char FoldPunctuation(char c) => c switch
    {
        '‘' or '’' or 'ʼ' or '´' or '`' => '\'',
        '“' or '”' or '″' => '"',
        '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
        _ => c,
    };
}
