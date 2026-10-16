using ChartShared.Core.Ini;
using ChartShared.Core.Resources;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Validation;

/// <summary>What reading a song's metadata found.</summary>
public enum SongMetadataStatus
{
    /// <summary>The metadata was read: a <c>.sng</c>'s, or a <c>song.ini</c>'s <c>[song]</c> section.</summary>
    Ok,

    /// <summary>The folder has no <c>song.ini</c>; saving creates one.</summary>
    NoSongIni,

    /// <summary>The <c>song.ini</c> has no <c>[song]</c> section; saving adds one.</summary>
    NoSongSection,

    /// <summary>The <c>song.ini</c> is too large to read, so it can't be edited.</summary>
    SongIniTooLarge,

    /// <summary>The <c>.sng</c> has structural errors: its metadata was read but it can't be rewritten.</summary>
    SngHasErrors,

    /// <summary>The song couldn't be read at all.</summary>
    Unreadable,
}

/// <summary>A song's metadata as stored, read for <see cref="MetadataValidator"/> and the editor.</summary>
/// <param name="Entry">The song.</param>
/// <param name="Pairs">Its pairs in stored order; a <c>.sng</c>'s keep any duplicates. Empty without a <c>song.ini</c> or <c>[song]</c> section.</param>
/// <param name="DuplicateKeys">The keys a <c>song.ini</c> repeats, which its reader merged out of <paramref name="Pairs"/>.</param>
/// <param name="Status">What the read found.</param>
/// <param name="ReadError">Why the metadata couldn't be read, or <see langword="null"/> when it was.</param>
/// <param name="SongIniPath">For a folder, the <c>song.ini</c> its metadata is saved to (which may not exist yet); <see langword="null"/> for a <c>.sng</c>.</param>
public sealed record SongMetadataReport(
    SongEntry Entry,
    IReadOnlyList<KeyValuePair<string, string>> Pairs,
    IReadOnlyList<string> DuplicateKeys,
    SongMetadataStatus Status,
    string? ReadError,
    string? SongIniPath = null)
{
    /// <summary>Whether the metadata can be edited and saved.</summary>
    public bool CanEdit => Status is SongMetadataStatus.Ok or SongMetadataStatus.NoSongIni or SongMetadataStatus.NoSongSection;
}

/// <summary>Reads a song's stored metadata, for the detail panel and the Validator alike, so the two agree on what's checked.</summary>
public static class SongMetadataCheck
{
    /// <summary>Reads <paramref name="entry"/>'s metadata. A song that can't be read is reported, not thrown.</summary>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<SongMetadataReport> ReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        var (report, files) = await OpenAsync(entry, cancellationToken).ConfigureAwait(false);
        files?.Dispose();
        return report;
    }

    /// <summary>
    /// Opens <paramref name="entry"/> and reads its metadata, leaving it open so its other files can be read without
    /// opening it again, as its chart is linted (<see cref="SongChartLint"/>). A song that can't be read is reported, not
    /// thrown, and has no files open.
    /// </summary>
    /// <returns>The metadata, and the song open, which the caller disposes; null when it couldn't be read.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<(SongMetadataReport Report, SongFileReader? Files)> OpenAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SongFileReader? files = null;
        try
        {
            // A .sng's index is read on the thread pool, as the caller may be the UI thread.
            files = entry.Kind == SongEntryKind.SngFile
                ? await Task.Run(() => SongFileReader.Open(entry, cancellationToken), cancellationToken).ConfigureAwait(false)
                : SongFileReader.Open(entry, cancellationToken);
            var report = files.Sng is { } sng
                ? FromSng(entry, sng)
                : await ReadFolderAsync(entry, SongIniIn(files), cancellationToken).ConfigureAwait(false);
            return (report, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files?.Dispose();
            return (new SongMetadataReport(entry, [], [], SongMetadataStatus.Unreadable, ex.Message), null);
        }
        catch
        {
            files?.Dispose();
            throw;
        }
    }

    // The folder's song.ini, found in its listing as SongIni.FindIn finds it.
    private static string? SongIniIn(SongFileReader files) =>
        files.TopLevel.FirstOrDefault(f => f.Name.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase)) is { } ini
            ? Path.Combine(files.Entry.FullPath, ini.Name)
            : null;

    /// <summary>The metadata of the <c>.sng</c> <paramref name="entry"/>, read as <paramref name="result"/>.</summary>
    public static SongMetadataReport FromSng(SongEntry entry, SngReadResult result)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(result);

        return new SongMetadataReport(
            entry,
            [.. result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))],
            [],
            result.HasErrors ? SongMetadataStatus.SngHasErrors : SongMetadataStatus.Ok,
            null);
    }

    /// <summary>Reads the song folder <paramref name="entry"/>'s metadata from <paramref name="songIniPath"/>.</summary>
    /// <param name="songIniPath">The folder's <c>song.ini</c>, as <see cref="SongIni.FindIn"/> finds it; <see langword="null"/> when it has none.</param>
    /// <exception cref="IOException">The <c>song.ini</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the <c>song.ini</c> is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<SongMetadataReport> ReadFolderAsync(SongEntry entry, string? songIniPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (songIniPath is null)
            return new SongMetadataReport(entry, [], [], SongMetadataStatus.NoSongIni, null, Path.Combine(entry.FullPath, SongIni.FileName));

        var ini = await SongIni.ReadAsync(songIniPath, cancellationToken).ConfigureAwait(false);
        return ini.Status switch
        {
            SongIniStatus.TooLarge => new SongMetadataReport(entry, [], [], SongMetadataStatus.SongIniTooLarge, FindingMessages.SongIniTooLarge, songIniPath),
            SongIniStatus.NoSongSection => new SongMetadataReport(entry, [], [], SongMetadataStatus.NoSongSection, null, songIniPath),
            _ => new SongMetadataReport(entry, ini.Pairs, ini.DuplicateKeys, SongMetadataStatus.Ok, null, songIniPath),
        };
    }
}
