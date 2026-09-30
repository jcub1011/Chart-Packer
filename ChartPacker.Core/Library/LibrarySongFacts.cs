using System.Globalization;
using System.Text.RegularExpressions;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Library;

/// <summary>The values a library song can be searched, sorted and filtered by.</summary>
/// <param name="Kind">Whether the song is a <c>.sng</c> file or a song folder.</param>
/// <param name="FileName">The file name without <c>.sng</c>, or the folder name.</param>
/// <param name="Title">The song's name, without rich-text tags.</param>
/// <param name="Artist">The artist, without rich-text tags.</param>
/// <param name="Album">The album, without rich-text tags.</param>
/// <param name="Genre">The genre, without rich-text tags.</param>
/// <param name="Charter">The charter, from <c>charter</c> or else the legacy <c>frets</c>, without rich-text tags.</param>
/// <param name="Year">The release year, the first four-digit number in <c>year</c>.</param>
/// <param name="LengthMs">The song length, or the longest audio file's when it has none.</param>
/// <param name="SizeBytes">The file's size, or the total of the folder's files.</param>
/// <param name="HasDrafts">The song has metadata changes that haven't been saved.</param>
/// <remarks>Every metadata value is <see langword="null"/> when it's unset or hasn't been read yet.</remarks>
public sealed partial record LibrarySongFacts(
    SongEntryKind Kind,
    string FileName,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? Genre = null,
    string? Charter = null,
    int? Year = null,
    long? LengthMs = null,
    ulong? SizeBytes = null,
    bool HasDrafts = false)
{
    private const string AlbumKey = "album";
    private const string GenreKey = "genre";
    private const string YearKey = "year";

    /// <summary>What's known about <paramref name="entry"/> before its metadata has been read.</summary>
    public static LibrarySongFacts For(SongEntry entry, bool hasDrafts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new LibrarySongFacts(entry.Kind, entry.DisplayName, HasDrafts: hasDrafts);
    }

    /// <summary>The facts for <paramref name="entry"/> given its metadata, with any unsaved changes already applied.</summary>
    /// <param name="fallbackLengthMs">The longest audio file's length, used when the metadata has no song length.</param>
    public static LibrarySongFacts From(
        SongEntry entry,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        ulong totalSize,
        long? fallbackLengthMs,
        bool hasDrafts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);

        var headline = SongHeadline.From(pairs);
        return new LibrarySongFacts(
            entry.Kind,
            entry.DisplayName,
            headline.Name,
            headline.Artist,
            MetadataPairs.Text(pairs, AlbumKey),
            MetadataPairs.Text(pairs, GenreKey),
            headline.Charter,
            ParseYear(MetadataPairs.Find(pairs, YearKey)),
            headline.LengthMs ?? fallbackLengthMs,
            totalSize,
            hasDrafts);
    }

    /// <summary>The first standalone four-digit number in <paramref name="value"/>, e.g. 2004 from <c>", 2004"</c> or <c>"2004-05-01"</c>.</summary>
    public static int? ParseYear(string? value) =>
        value is not null && YearPattern().Match(value) is { Success: true } match
            ? int.Parse(match.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex YearPattern();
}
