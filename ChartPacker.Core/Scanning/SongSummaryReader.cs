using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Scanning;

/// <summary>What the library list shows for a song: its stored metadata and how big it is.</summary>
/// <param name="Pairs">The stored metadata pairs in order; empty when a folder has no readable <c>[song]</c> section.</param>
/// <param name="TotalSize">The <c>.sng</c> file's size, or the sum of every file in the folder, in bytes.</param>
/// <param name="FallbackLengthMs">
/// The longest audio file's length in milliseconds, measured only when <paramref name="Pairs"/> has
/// no usable <c>song_length</c>; otherwise <see langword="null"/>.
/// </param>
public sealed record SongSummary(IReadOnlyList<KeyValuePair<string, string>> Pairs, ulong TotalSize, long? FallbackLengthMs = null);

/// <summary>Reads a <see cref="SongSummary"/> for a library entry.</summary>
public static class SongSummaryReader
{
    /// <summary>Reads <paramref name="entry"/>'s summary on the thread pool.</summary>
    /// <remarks>
    /// A <c>.sng</c> with structural problems still gives whatever metadata could be read. A folder's
    /// metadata comes from the <c>song.ini</c> directly in it, matched ignoring case. When the
    /// metadata has no usable <c>song_length</c>, the audio files are measured with
    /// <see cref="AudioLength"/>.
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
            ulong sngSize = (ulong)new FileInfo(entry.FullPath).Length;
            if (HasLength(pairs))
                return new SongSummary(pairs, sngSize);

            using SafeFileHandle handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new SongSummary(pairs, sngSize, AudioLength.LongestInSng(handle, result, cancellationToken));
        }

        var files = SongFolderListing.List(entry.FullPath, cancellationToken);
        ulong size = (ulong)files.Sum(f => f.Length);

        var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<KeyValuePair<string, string>> folderPairs = songIni is null
            ? []
            : (await SongIni.ReadAsync(Path.Combine(entry.FullPath, songIni.RelativePath), cancellationToken).ConfigureAwait(false)).Pairs;

        return HasLength(folderPairs)
            ? new SongSummary(folderPairs, size)
            : new SongSummary(folderPairs, size, AudioLength.LongestInFolder(entry.FullPath, files, cancellationToken));
    }

    private static bool HasLength(IReadOnlyList<KeyValuePair<string, string>> pairs) => SongHeadline.From(pairs).LengthMs is not null;
}
