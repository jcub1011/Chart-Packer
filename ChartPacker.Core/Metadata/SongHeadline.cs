using System.Globalization;
using ChartPacker.Core.Text;

namespace ChartPacker.Core.Metadata;

/// <summary>The few metadata values the library list shows for a song.</summary>
/// <param name="Name">The song's title, without rich-text tags.</param>
/// <param name="Artist">The artist, without rich-text tags.</param>
/// <param name="Charter">The charter, from <c>charter</c> or else the legacy <c>frets</c>, without rich-text tags.</param>
/// <param name="LengthMs">The song length in milliseconds; <see langword="null"/> when unset, zero or not a number.</param>
public sealed record SongHeadline(string? Name, string? Artist, string? Charter, long? LengthMs)
{
    private const string NameKey = "name";
    private const string ArtistKey = "artist";
    private const string CharterKey = "charter";
    private const string LegacyCharterKey = "frets";
    private const string LengthKey = "song_length";

    /// <summary>Picks the headline values out of <paramref name="pairs"/>.</summary>
    /// <remarks>
    /// Keys are matched ignoring case, and with duplicate keys the first one is used, as the
    /// metadata table shows it. Blank text values count as unset.
    /// </remarks>
    public static SongHeadline From(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        return new SongHeadline(
            Text(pairs, NameKey),
            Text(pairs, ArtistKey),
            Text(pairs, CharterKey) ?? Text(pairs, LegacyCharterKey),
            Length(pairs));
    }

    /// <summary>
    /// The charter as stored (from <c>charter</c> or else the legacy <c>frets</c>), trimmed, with its rich-text tags
    /// left in for a view to render; <see langword="null"/> when there is no visible text.
    /// </summary>
    public static string? CharterMarkup(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        return Markup(pairs, CharterKey) ?? Markup(pairs, LegacyCharterKey);
    }

    private static string? Markup(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
        Find(pairs, key) is { } value && RichText.ToPlainText(value).Trim().Length > 0 ? value.Trim() : null;

    private static string? Text(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) => MetadataPairs.Text(pairs, key);

    private static long? Length(IReadOnlyList<KeyValuePair<string, string>> pairs) =>
        Find(pairs, LengthKey) is { } value
        && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ms)
        && ms > 0
            ? ms
            : null;

    private static string? Find(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) => MetadataPairs.Find(pairs, key);
}
