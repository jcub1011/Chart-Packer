using System.Text.RegularExpressions;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Dedup;

/// <summary>Chooses which copy of a duplicate to keep by default.</summary>
/// <remarks>
/// In order, until one copy is left: a copy with unsaved edits (it can't be deleted); in a group
/// of folders and <c>.sng</c> files, the preferred kind; a name without a download suffix such as
/// <c> (1)</c> or <c> - Copy</c>; the shortest path; the oldest; and the first path in ordinal order.
/// </remarks>
public static partial class KeeperRules
{
    /// <summary>The copy to keep among <paramref name="candidates"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="candidates"/> is empty.</exception>
    public static SongFingerprint Choose(IReadOnlyCollection<SongFingerprint> candidates, DedupKindPreference preference)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("There's nothing to choose from.", nameof(candidates));

        var comparer = new KeeperComparer(candidates, preference);
        return candidates.Order(comparer).First();
    }

    /// <summary>The variant to keep among <paramref name="variants"/>: the one holding the copy <see cref="Choose(IReadOnlyCollection{SongFingerprint}, DedupKindPreference)"/> picks among all of them.</summary>
    /// <exception cref="ArgumentException"><paramref name="variants"/> holds no copies.</exception>
    public static DedupVariant Choose(IReadOnlyCollection<DedupVariant> variants, DedupKindPreference preference)
    {
        ArgumentNullException.ThrowIfNull(variants);
        var keeper = Choose([.. variants.SelectMany(v => v.Entries)], preference);
        return variants.First(v => v.Entries.Contains(keeper));
    }

    /// <summary>
    /// Whether <paramref name="displayName"/> ends like a second download or a copy made by a file
    /// manager: <c>(1)</c>, <c>[1]</c>, <c> - Copy</c>, <c> - Copy (2)</c>, <c>(copy)</c>,
    /// <c>(another copy)</c> or <c>(2nd copy)</c>, ignoring case, or Finder's lowercase <c> copy</c>
    /// and <c> copy 2</c>.
    /// </summary>
    /// <remarks>
    /// Numbers have at most two digits, so a year such as <c>(2004)</c> isn't a suffix, and a bare
    /// <c> copy</c> must be lowercase, so a title such as "Carbon Copy" isn't either.
    /// </remarks>
    public static bool HasDownloadSuffix(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return DownloadSuffix().IsMatch(displayName);
    }

    [GeneratedRegex(
        @"(\s*\(\d{1,2}\)|\s*\[\d{1,2}\]|\s+-\s+copy(\s+\(\d{1,2}\))?|(?-i:\s+copy(\s+\d{1,2})?)|\s*\((another\s+|\d{1,2}(st|nd|rd|th)\s+)?copy\))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadSuffix();

    private sealed class KeeperComparer : IComparer<SongFingerprint>
    {
        private readonly SongEntryKind? _preferred;

        public KeeperComparer(IReadOnlyCollection<SongFingerprint> candidates, DedupKindPreference preference)
        {
            bool mixed = candidates.Select(c => c.Entry.Kind).Distinct().Skip(1).Any();
            _preferred = !mixed ? null : preference == DedupKindPreference.PreferFolder ? SongEntryKind.SongFolder : SongEntryKind.SngFile;
        }

        public int Compare(SongFingerprint? x, SongFingerprint? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return 1;
            if (y is null)
                return -1;

            int result = y.HasDrafts.CompareTo(x.HasDrafts);
            if (result == 0 && _preferred is { } kind)
                result = (y.Entry.Kind == kind).CompareTo(x.Entry.Kind == kind);
            if (result == 0)
                result = HasDownloadSuffix(x.Entry.DisplayName).CompareTo(HasDownloadSuffix(y.Entry.DisplayName));
            if (result == 0)
                result = x.Entry.FullPath.Length.CompareTo(y.Entry.FullPath.Length);
            if (result == 0)
                result = x.Stamp.LastWriteTimeUtc.CompareTo(y.Stamp.LastWriteTimeUtc);
            if (result == 0)
                result = string.CompareOrdinal(x.Entry.FullPath, y.Entry.FullPath);
            return result;
        }
    }
}
