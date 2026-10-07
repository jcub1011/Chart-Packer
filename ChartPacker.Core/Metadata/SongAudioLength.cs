using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Metadata;

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
    public static long? LongestMs(SongEntry? entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        long? longest = null;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase) || (entry is null && !track.IsStaged))
                continue;

            try
            {
                using var stream = track.StagedSourcePath is { } staged
                    ? new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024)
                    : SongFile.OpenRead(entry!, track.StoredName, cancellationToken);
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
