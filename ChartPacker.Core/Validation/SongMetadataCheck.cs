using ChartPacker.Core.Ini;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Validation;

/// <summary>A song's metadata as stored, read for <see cref="MetadataValidator"/>.</summary>
/// <param name="Entry">The song.</param>
/// <param name="Pairs">Its pairs in stored order; a <c>.sng</c>'s keep any duplicates. Empty without a <c>song.ini</c> or <c>[song]</c> section.</param>
/// <param name="DuplicateKeys">The keys a <c>song.ini</c> repeats, which its reader merged out of <paramref name="Pairs"/>.</param>
/// <param name="ReadError">Why the metadata couldn't be read, or <see langword="null"/> when it was.</param>
/// <param name="SongIniPath">For a folder, the <c>song.ini</c> its metadata is saved to (which may not exist yet); <see langword="null"/> for a <c>.sng</c>.</param>
public sealed record SongMetadataReport(
    SongEntry Entry,
    IReadOnlyList<KeyValuePair<string, string>> Pairs,
    IReadOnlyList<string> DuplicateKeys,
    string? ReadError,
    string? SongIniPath = null);

/// <summary>Reads a song's stored metadata the way the detail panel does, so the two agree on what's checked.</summary>
public static class SongMetadataCheck
{
    /// <summary>Reads <paramref name="entry"/>'s metadata. A song that can't be read is reported, not thrown.</summary>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<SongMetadataReport> ReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            if (entry.Kind == SongEntryKind.SngFile)
            {
                var result = await SngReader.ReadAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
                return new SongMetadataReport(entry, [.. result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))], [], null);
            }

            // Only a song.ini directly in the folder counts; one in a subfolder belongs to something else.
            string? songIni = Directory.EnumerateFiles(entry.FullPath)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
            if (songIni is null)
                return new SongMetadataReport(entry, [], [], null, Path.Combine(entry.FullPath, SongIni.FileName));

            var ini = await SongIni.ReadAsync(songIni, cancellationToken).ConfigureAwait(false);
            return ini.Status == SongIniStatus.TooLarge
                ? new SongMetadataReport(entry, [], [], FindingMessages.SongIniTooLarge, songIni)
                : new SongMetadataReport(entry, ini.Pairs, ini.DuplicateKeys, null, songIni);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SongMetadataReport(entry, [], [], ex.Message);
        }
    }
}
