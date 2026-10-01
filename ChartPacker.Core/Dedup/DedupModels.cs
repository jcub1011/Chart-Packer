using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Dedup;

/// <summary>How alike the copies in a duplicate group are.</summary>
public enum DedupTier
{
    /// <summary>Every stored file and all the metadata match.</summary>
    Exact,

    /// <summary>The chart matches, but the metadata or other files differ.</summary>
    SameChart,

    /// <summary>The same artist and title, with different charts.</summary>
    MergeCandidate,
}

/// <summary>Which copy to keep by default when a group holds both a folder and a <c>.sng</c>.</summary>
public enum DedupKindPreference
{
    /// <summary>Keep the <c>.sng</c>.</summary>
    PreferSng,

    /// <summary>Keep the folder.</summary>
    PreferFolder,
}

/// <summary>What a song's files on disk looked like when they were read, to tell whether they've changed since.</summary>
/// <param name="TotalBytes">The <c>.sng</c>'s size, or the size of every file in the folder.</param>
/// <param name="LastWriteTimeUtc">The <c>.sng</c>'s write time, or the newest write time in the folder.</param>
/// <param name="FileCount">1 for a <c>.sng</c>, or the number of files in the folder.</param>
/// <param name="ListingDigest">A folder's listing as a hash of every path, size and write time; empty for a <c>.sng</c>.</param>
public sealed record EntryStamp(long TotalBytes, DateTime LastWriteTimeUtc, int FileCount, string ListingDigest);

/// <summary>A file a song holds, under the name a <c>.sng</c> stores it as.</summary>
/// <param name="Name">The stored name, with registered names lowercased.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record StoredFileInfo(string Name, long Length);

/// <summary>Why a song was left out of the comparison.</summary>
public enum DedupSkipReason
{
    /// <summary>It couldn't be read.</summary>
    Unreadable,

    /// <summary>The <c>.sng</c> is damaged.</summary>
    SngHasErrors,

    /// <summary>The folder's <c>song.ini</c> is too large to read.</summary>
    SongIniTooLarge,

    /// <summary>It changed on disk while it was being compared.</summary>
    ChangedDuringAnalysis,
}

/// <summary>A song that was left out of the comparison.</summary>
public sealed record DedupSkipped(SongEntry Entry, DedupSkipReason Reason, string? Detail);

/// <summary>Where one stored file's bytes are read from.</summary>
/// <param name="SourcePath">A folder file's path; <see langword="null"/> for a file in a <c>.sng</c>.</param>
/// <param name="SngEntry">A <c>.sng</c> file's index entry; <see langword="null"/> for a folder file.</param>
/// <param name="LastWriteTimeUtc">A folder file's write time when it was listed.</param>
internal sealed record StoredFileSource(string? SourcePath, SngFileEntry? SngEntry, long Length, DateTime LastWriteTimeUtc);

/// <summary>Everything the duplicate finder compares about one song, read without its file contents.</summary>
public sealed class SongFingerprint
{
    internal SongFingerprint(
        SongEntry entry,
        EntryStamp stamp,
        IReadOnlyList<StoredFileInfo> files,
        IReadOnlyList<string> extras,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        bool hasDrafts,
        ReadOnlyMemory<byte> mask,
        IReadOnlyDictionary<string, StoredFileSource> sources)
    {
        Entry = entry;
        Stamp = stamp;
        Files = files;
        Extras = extras;
        Metadata = metadata;
        HasDrafts = hasDrafts;
        Mask = mask;
        Sources = sources;
        MetadataKey = StrictMetadata.Key(metadata);
        Headline = SongHeadline.From(metadata);
        SongKey = SongKeys.For(Headline.Artist, Headline.Name);
        ChartName = ChartReader.Pick(files.Where(f => !f.Name.Contains('/')).Select(f => f.Name));
        ChartLength = ChartName is null ? null : files.First(f => f.Name.Equals(ChartName, StringComparison.OrdinalIgnoreCase)).Length;
    }

    /// <summary>The song.</summary>
    public SongEntry Entry { get; }

    /// <summary>How its files looked when they were read.</summary>
    public EntryStamp Stamp { get; }

    /// <summary>The files a <c>.sng</c> holds, or would hold if the folder were packed with its subfolders, sorted by name.</summary>
    public IReadOnlyList<StoredFileInfo> Files { get; }

    /// <summary>
    /// A folder's files that a <c>.sng</c> can't hold, such as nested <c>.sng</c> files or badly named
    /// files, as paths from the folder. <c>song.ini</c> is listed when some of its pairs can't be stored.
    /// </summary>
    public IReadOnlyList<string> Extras { get; }

    /// <summary>The metadata pairs as stored, with empty values dropped.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Metadata { get; }

    /// <summary>A hash of the metadata that is equal only when every key and value is.</summary>
    public string MetadataKey { get; }

    /// <summary>The title, artist and charter.</summary>
    public SongHeadline Headline { get; }

    /// <summary>The artist and title folded for comparison; <see langword="null"/> when either is missing.</summary>
    public string? SongKey { get; }

    /// <summary>The chart the song plays, or <see langword="null"/> when it has none.</summary>
    public string? ChartName { get; }

    /// <summary>The chart's size in bytes.</summary>
    public long? ChartLength { get; }

    /// <summary>Whether the song has unsaved metadata edits.</summary>
    public bool HasDrafts { get; }

    internal ReadOnlyMemory<byte> Mask { get; }

    internal IReadOnlyDictionary<string, StoredFileSource> Sources { get; }
}

/// <summary>Songs whose files and metadata are all identical.</summary>
public sealed class DedupVariant(IReadOnlyList<SongFingerprint> entries)
{
    /// <summary>The identical songs; at least one.</summary>
    public IReadOnlyList<SongFingerprint> Entries { get; } = entries;
}

/// <summary>Variants that share one chart.</summary>
public sealed class DedupChart(string chartName, string? notesHash, IReadOnlyList<DedupVariant> variants)
{
    /// <summary>The chart's stored name.</summary>
    public string ChartName { get; } = chartName;

    /// <summary>The chart's hash; <see langword="null"/> when no other variant could share it, so it wasn't needed.</summary>
    public string? NotesHash { get; } = notesHash;

    /// <summary>The variants; at least one.</summary>
    public IReadOnlyList<DedupVariant> Variants { get; } = variants;

    /// <summary>Every song with this chart.</summary>
    public IEnumerable<SongFingerprint> Entries => Variants.SelectMany(v => v.Entries);
}

/// <summary>Different charts of one song.</summary>
public sealed class DedupSong(string songKey, IReadOnlyList<DedupChart> charts)
{
    /// <summary>The folded artist and title the charts share.</summary>
    public string SongKey { get; } = songKey;

    /// <summary>The charts; at least two.</summary>
    public IReadOnlyList<DedupChart> Charts { get; } = charts;
}

/// <summary>What the duplicate finder found.</summary>
/// <param name="Exact">Variants with two or more identical songs.</param>
/// <param name="SameChart">Charts shared by two or more variants.</param>
/// <param name="MergeCandidates">Songs with two or more charts.</param>
/// <param name="Skipped">Songs that couldn't be compared.</param>
/// <param name="Hasher">The hashes taken so far, for comparing the songs further.</param>
public sealed record DedupAnalysis(
    IReadOnlyList<DedupVariant> Exact,
    IReadOnlyList<DedupChart> SameChart,
    IReadOnlyList<DedupSong> MergeCandidates,
    IReadOnlyList<DedupSkipped> Skipped,
    ContentHasher Hasher);

/// <summary>A stage of the duplicate finder.</summary>
public enum DedupPhase
{
    /// <summary>Reading each song's file list and metadata.</summary>
    Reading,

    /// <summary>Hashing the files of songs that might be duplicates.</summary>
    Comparing,

    /// <summary>Hashing the charts of songs that might share one.</summary>
    MatchingCharts,
}

/// <summary>How far the duplicate finder has got.</summary>
/// <param name="Phase">The stage.</param>
/// <param name="ItemsDone">Songs read, or candidate groups compared, in this stage.</param>
/// <param name="ItemsTotal">Songs to read, or candidate groups to compare, in this stage; it doesn't change within a stage.</param>
/// <param name="BytesHashed">Bytes hashed so far.</param>
public sealed record DedupProgress(DedupPhase Phase, int ItemsDone, int ItemsTotal, long BytesHashed);
