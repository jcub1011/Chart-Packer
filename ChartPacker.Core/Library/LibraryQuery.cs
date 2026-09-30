using System.Globalization;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Library;

/// <summary>What the library list can be sorted by.</summary>
public enum LibrarySortField
{
    /// <summary>The order the scan found the songs in.</summary>
    ScanOrder,
    Title,
    Artist,
    Album,
    Genre,
    Charter,
    Year,
    Length,
    Size,

    /// <summary>.sng files before song folders.</summary>
    Type,
    FileName,
}

/// <summary>A search, a set of filters and a sort order for the library list.</summary>
/// <remarks>
/// Every filter must pass for a song to show. A filter on a value the song doesn't have (it's unset,
/// or hasn't been read yet) fails. Text is matched ignoring case and accents. A set filter, such as
/// <see cref="Instruments"/>, rejects a song with any excluded item, then needs every included item
/// or at least one of them, as its <see cref="SetMatch"/> says.
/// </remarks>
public sealed record LibraryQuery
{
    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>No search, no filters, in scan order.</summary>
    public static LibraryQuery Default { get; } = new();

    /// <summary>Words that must each appear in the file name, title or artist.</summary>
    public string Search { get; init; } = string.Empty;

    public LibrarySortField SortField { get; init; }

    public bool Descending { get; init; }

    public bool IncludeSng { get; init; } = true;

    public bool IncludeFolders { get; init; } = true;

    public string? TitleContains { get; init; }

    public string? ArtistContains { get; init; }

    public string? AlbumContains { get; init; }

    public string? GenreContains { get; init; }

    public string? CharterContains { get; init; }

    public int? YearMin { get; init; }

    public int? YearMax { get; init; }

    public long? LengthMinMs { get; init; }

    public long? LengthMaxMs { get; init; }

    public ulong? SizeMinBytes { get; init; }

    public ulong? SizeMaxBytes { get; init; }

    /// <summary>Only songs with metadata changes that haven't been saved.</summary>
    public bool OnlyUnsaved { get; init; }

    /// <summary>The parts the chart must or mustn't have notes for.</summary>
    public SetFilter<LibraryInstruments> Instruments { get; init; }

    /// <summary>The difficulties the chart must or mustn't have notes for, on any part.</summary>
    public SetFilter<LibraryDifficulties> Difficulties { get; init; }

    /// <summary>The audio stems the song must or mustn't have.</summary>
    public SetFilter<LibraryStems> Stems { get; init; }

    public FilterPresence Video { get; init; }

    public FilterPresence BackgroundImage { get; init; }

    public FilterPresence AlbumArt { get; init; }

    /// <summary>How many filters narrow the list; the search isn't counted. A range counts once.</summary>
    public int ActiveFilterCount =>
        (IncludeSng && IncludeFolders ? 0 : 1)
        + (IsSet(TitleContains) ? 1 : 0)
        + (IsSet(ArtistContains) ? 1 : 0)
        + (IsSet(AlbumContains) ? 1 : 0)
        + (IsSet(GenreContains) ? 1 : 0)
        + (IsSet(CharterContains) ? 1 : 0)
        + (YearMin is not null || YearMax is not null ? 1 : 0)
        + (LengthMinMs is not null || LengthMaxMs is not null ? 1 : 0)
        + (SizeMinBytes is not null || SizeMaxBytes is not null ? 1 : 0)
        + (OnlyUnsaved ? 1 : 0)
        + (Instruments.IsActive ? 1 : 0)
        + (Difficulties.IsActive ? 1 : 0)
        + (Stems.IsActive ? 1 : 0)
        + (Video != FilterPresence.Either ? 1 : 0)
        + (BackgroundImage != FilterPresence.Either ? 1 : 0)
        + (AlbumArt != FilterPresence.Either ? 1 : 0);

    /// <summary>The search or a filter may hide songs.</summary>
    public bool IsFiltering => ActiveFilterCount > 0 || !string.IsNullOrWhiteSpace(Search);

    /// <summary>Every song shows, in scan order.</summary>
    public bool KeepsScanOrder => !IsFiltering && SortField == LibrarySortField.ScanOrder && !Descending;

    /// <summary>Whether <paramref name="facts"/> passes the search and every filter.</summary>
    public bool Matches(LibrarySongFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Matches(facts, SplitWords(Search));
    }

    /// <summary>The items that match, sorted. Ties, and songs missing the sorted value, keep scan order; the missing ones go last.</summary>
    /// <param name="scanOrdered">Every item, in scan order.</param>
    public List<T> Apply<T>(IReadOnlyList<T> scanOrdered, Func<T, LibrarySongFacts> factsOf)
    {
        ArgumentNullException.ThrowIfNull(scanOrdered);
        ArgumentNullException.ThrowIfNull(factsOf);

        var words = SplitWords(Search);
        var matches = new List<(T Item, LibrarySongFacts Facts, int Index)>(scanOrdered.Count);
        for (int i = 0; i < scanOrdered.Count; i++)
        {
            var facts = factsOf(scanOrdered[i]);
            if (Matches(facts, words))
                matches.Add((scanOrdered[i], facts, i));
        }

        if (SortField == LibrarySortField.ScanOrder)
        {
            if (Descending)
                matches.Reverse();
        }
        else
        {
            var compare = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            int direction = Descending ? -1 : 1;
            matches.Sort((a, b) =>
            {
                int result = CompareBy(a.Facts, b.Facts, compare, direction);
                return result != 0 ? result : a.Index.CompareTo(b.Index);
            });
        }

        return matches.ConvertAll(m => m.Item);
    }

    private int CompareBy(LibrarySongFacts a, LibrarySongFacts b, StringComparer compare, int direction) => SortField switch
    {
        LibrarySortField.Title => Compare(a.Title, b.Title, compare, direction),
        LibrarySortField.Artist => Compare(a.Artist, b.Artist, compare, direction),
        LibrarySortField.Album => Compare(a.Album, b.Album, compare, direction),
        LibrarySortField.Genre => Compare(a.Genre, b.Genre, compare, direction),
        LibrarySortField.Charter => Compare(a.Charter, b.Charter, compare, direction),
        LibrarySortField.Year => Compare(a.Year, b.Year, direction),
        LibrarySortField.Length => Compare(a.LengthMs, b.LengthMs, direction),
        LibrarySortField.Size => Compare(a.SizeBytes, b.SizeBytes, direction),
        LibrarySortField.Type => direction * a.Kind.CompareTo(b.Kind),
        LibrarySortField.FileName => direction * compare.Compare(a.FileName, b.FileName),
        _ => 0,
    };

    /// <summary>Missing values go last in either direction.</summary>
    private static int Compare(string? a, string? b, StringComparer compare, int direction) =>
        (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => direction * compare.Compare(a, b),
        };

    /// <summary>Missing values go last in either direction.</summary>
    private static int Compare<TValue>(TValue? a, TValue? b, int direction)
        where TValue : struct, IComparable<TValue> =>
        (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => direction * a.Value.CompareTo(b.Value),
        };

    private bool Matches(LibrarySongFacts facts, string[] words)
    {
        if (!(facts.Kind == SongEntryKind.SngFile ? IncludeSng : IncludeFolders))
            return false;
        if (OnlyUnsaved && !facts.HasDrafts)
            return false;

        foreach (string word in words)
        {
            if (!Contains(facts.FileName, word) && !Contains(facts.Title, word) && !Contains(facts.Artist, word))
                return false;
        }

        return ContainsFilter(facts.Title, TitleContains)
            && ContainsFilter(facts.Artist, ArtistContains)
            && ContainsFilter(facts.Album, AlbumContains)
            && ContainsFilter(facts.Genre, GenreContains)
            && ContainsFilter(facts.Charter, CharterContains)
            && InRange(facts.Year, YearMin, YearMax)
            && InRange(facts.LengthMs, LengthMinMs, LengthMaxMs)
            && InRange(facts.SizeBytes, SizeMinBytes, SizeMaxBytes)
            && Instruments.Matches(facts.Instruments)
            && Difficulties.Matches(facts.Difficulties)
            && Stems.Matches(facts.Stems)
            && LibraryContentFilters.PresenceMatches(Video, facts.HasVideo)
            && LibraryContentFilters.PresenceMatches(BackgroundImage, facts.HasImage)
            && LibraryContentFilters.PresenceMatches(AlbumArt, facts.HasAlbumArt);
    }

    private static bool ContainsFilter(string? value, string? filter) =>
        !IsSet(filter) || Contains(value, filter!.Trim());

    private static bool Contains(string? value, string text) =>
        value is not null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(value, text, MatchOptions) >= 0;

    private static bool InRange<TValue>(TValue? value, TValue? min, TValue? max)
        where TValue : struct, IComparable<TValue>
    {
        if (min is null && max is null)
            return true;

        return value is { } v
            && (min is not { } lo || v.CompareTo(lo) >= 0)
            && (max is not { } hi || v.CompareTo(hi) <= 0);
    }

    private static bool IsSet(string? filter) => !string.IsNullOrWhiteSpace(filter);

    private static string[] SplitWords(string search) =>
        search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
