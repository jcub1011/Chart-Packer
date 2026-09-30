using ChartPacker.Core.Ini;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Dedup;

/// <summary>A song can't be fingerprinted.</summary>
internal sealed class FingerprintSkippedException(DedupSkipReason reason, string? detail) : Exception(detail)
{
    public DedupSkipReason Reason { get; } = reason;
}

/// <summary>Reads a song's <see cref="SongFingerprint"/>: its file list and metadata, without any file contents but <c>song.ini</c>.</summary>
internal static class SongFingerprintReader
{
    /// <summary>
    /// Folders are planned with their subfolders, so a folder whose subfolders a <c>.sng</c> holds
    /// too compares equal to it, and one whose subfolders it lacks doesn't.
    /// </summary>
    private static readonly PackOptions FolderOptions = new(IncludeSubfolders: true);

    /// <summary>Metadata rules whose findings mean a folder's <c>song.ini</c> holds pairs a <c>.sng</c> can't.</summary>
    private static readonly HashSet<string> UnstorableMetadataRules = [SngRules.Sng020, SngRules.Sng021, SngRules.Sng023];

    /// <exception cref="FingerprintSkippedException">The song can't be compared.</exception>
    /// <exception cref="IOException">The song can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static Task<SongFingerprint> ReadAsync(SongEntry entry, bool hasDrafts, CancellationToken cancellationToken) =>
        entry.Kind == SongEntryKind.SngFile
            ? Task.Run(() => ReadSng(entry, hasDrafts, cancellationToken), cancellationToken)
            : ReadFolderAsync(entry, hasDrafts, cancellationToken);

    private static SongFingerprint ReadSng(SongEntry entry, bool hasDrafts, CancellationToken cancellationToken)
    {
        var info = new FileInfo(entry.FullPath);
        if (!info.Exists)
            throw new FileNotFoundException($"'{entry.FullPath}' no longer exists.", entry.FullPath);

        var stamp = EntryStamps.ForFile(info.Length, info.LastWriteTimeUtc);
        var read = SngReader.Read(entry.FullPath, cancellationToken);
        if (read.HasErrors || read.Header is not { } header)
            throw new FingerprintSkippedException(DedupSkipReason.SngHasErrors, read.Findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message);

        var files = new List<StoredFileInfo>(read.Files.Count);
        var sources = new Dictionary<string, StoredFileSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in read.Files)
        {
            string name = FilenameRules.NormaliseStoredName(file.Name);
            if (!sources.TryAdd(name, new StoredFileSource(null, file, (long)file.Length, stamp.LastWriteTimeUtc)))
                throw new FingerprintSkippedException(DedupSkipReason.SngHasErrors, $"'{file.Name}' is stored twice.");
            files.Add(new StoredFileInfo(name, (long)file.Length));
        }

        var metadata = read.Metadata.Where(p => p.Value.Length > 0).Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();
        return new SongFingerprint(entry, stamp, Sorted(files), [], metadata, hasDrafts, header.Mask, sources);
    }

    private static async Task<SongFingerprint> ReadFolderAsync(SongEntry entry, bool hasDrafts, CancellationToken cancellationToken)
    {
        string folder = Path.GetFullPath(entry.FullPath);
        var listing = await Task.Run(() => SongFolderListing.ListStamped(folder, cancellationToken), cancellationToken).ConfigureAwait(false);
        var plan = await PackPlanner.PlanAsync(
            folder,
            [.. listing.Select(f => new FolderFileEntry(f.RelativePath, f.Length))],
            FolderOptions,
            cancellationToken).ConfigureAwait(false);

        if (plan.Findings.FirstOrDefault(f => f.RuleId == SngRules.Sng071 && f.Severity == Severity.Error) is { } tooLarge)
            throw new FingerprintSkippedException(DedupSkipReason.SongIniTooLarge, tooLarge.Message);

        var stamps = listing.ToDictionary(f => Path.Combine(folder, f.RelativePath), StringComparer.Ordinal);
        var files = new List<StoredFileInfo>(plan.Included.Count);
        var sources = new Dictionary<string, StoredFileSource>(StringComparer.OrdinalIgnoreCase);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in plan.Included)
        {
            var stamp = stamps[file.SourcePath];
            included.Add(stamp.RelativePath);
            sources[file.StoredName] = new StoredFileSource(file.SourcePath, null, file.Length, stamp.LastWriteTimeUtc);
            files.Add(new StoredFileInfo(file.StoredName, file.Length));
        }

        var extras = new List<string>();
        foreach (var file in listing)
        {
            if (included.Contains(file.RelativePath)
                || file.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase)
                || file.RelativePath.Split('/').Any(FilenameRules.IsJunk))
                continue;
            extras.Add(file.RelativePath);
        }

        if (plan.Findings.Any(f => UnstorableMetadataRules.Contains(f.RuleId)))
            extras.Add(SongIni.FileName);

        return new SongFingerprint(entry, EntryStamps.ForListing(listing), Sorted(files), extras, plan.Metadata, hasDrafts, ReadOnlyMemory<byte>.Empty, sources);
    }

    private static List<StoredFileInfo> Sorted(List<StoredFileInfo> files)
    {
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return files;
    }
}
