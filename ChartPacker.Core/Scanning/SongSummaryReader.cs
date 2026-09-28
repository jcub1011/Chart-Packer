using ChartPacker.Core.Ini;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Scanning;

/// <summary>What the library list shows for a song: its stored metadata and how big it is.</summary>
/// <param name="Pairs">The stored metadata pairs in order; empty when a folder has no readable <c>[song]</c> section.</param>
/// <param name="TotalSize">The <c>.sng</c> file's size, or the sum of every file in the folder, in bytes.</param>
public sealed record SongSummary(IReadOnlyList<KeyValuePair<string, string>> Pairs, ulong TotalSize);

/// <summary>Reads a <see cref="SongSummary"/> for a library entry.</summary>
public static class SongSummaryReader
{
    /// <summary>Reads <paramref name="entry"/>'s summary on the thread pool.</summary>
    /// <remarks>
    /// A <c>.sng</c> with structural problems still gives whatever metadata could be read. A folder's
    /// metadata comes from the <c>song.ini</c> directly in it, matched ignoring case.
    /// </remarks>
    /// <exception cref="IOException">The file or folder can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<SongSummary> ReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => ReadCoreAsync(entry, cancellationToken), cancellationToken);
    }

    private static async Task<SongSummary> ReadCoreAsync(SongEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
        {
            var result = SngReader.Read(entry.FullPath, cancellationToken);
            var pairs = result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();
            return new SongSummary(pairs, (ulong)new FileInfo(entry.FullPath).Length);
        }

        var files = SongFolderListing.List(entry.FullPath, cancellationToken);
        ulong size = (ulong)files.Sum(f => f.Length);

        var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        if (songIni is null)
            return new SongSummary([], size);

        var ini = await SongIni.ReadAsync(Path.Combine(entry.FullPath, songIni.RelativePath), cancellationToken).ConfigureAwait(false);
        return new SongSummary(ini.Pairs, size);
    }
}
