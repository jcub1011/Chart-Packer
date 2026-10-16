using ChartShared.Core.Scanning;

namespace ChartShared.Core.Metadata;

/// <summary>How long a song's audio is: its longest stem, as <c>song_length</c> holds it.</summary>
public static class SongAudioLength
{
    /// <summary>
    /// The longest of <paramref name="tracks"/> in milliseconds, other than the preview clip; null when none can be
    /// measured. A staged track is read from where it is, and the others from <paramref name="entry"/>.
    /// </summary>
    /// <param name="entry">The song the tracks that aren't staged are in; null when every track is staged.</param>
    /// <remarks>A track that can't be read doesn't hide the others.</remarks>
    /// <exception cref="OperationCanceledException">Measuring was cancelled.</exception>
    public static long? LongestMs(SongEntry? entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken = default) =>
        Longest(tracks, entry is null ? null : name => SongFile.OpenRead(entry, name, cancellationToken), cancellationToken);

    /// <summary>
    /// As <see cref="LongestMs(SongEntry?, IReadOnlyList{SongTrack}, CancellationToken)"/>, with the tracks that aren't
    /// staged read through <paramref name="files"/>, so a <c>.sng</c> isn't opened again for each.
    /// </summary>
    /// <exception cref="OperationCanceledException">Measuring was cancelled.</exception>
    public static long? LongestMs(SongFileReader files, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Longest(tracks, files.OpenRead, cancellationToken);
    }

    // openStored opens a track in the song; null when there's no song, so only staged tracks count.
    private static long? Longest(IReadOnlyList<SongTrack> tracks, Func<string, Stream?>? openStored, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        long? longest = null;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase) || (openStored is null && !track.IsStaged))
                continue;

            try
            {
                using var stream = track.StagedSourcePath is { } staged
                    ? new PooledReadStream(new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0), 64 * 1024)
                    : openStored!(track.StoredName);
                if (stream is not null && AudioLength.MeasureMs(stream, track.StoredName) is { } ms)
                    longest = Math.Max(longest ?? 0, ms);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
            }
        }

        return longest;
    }
}
