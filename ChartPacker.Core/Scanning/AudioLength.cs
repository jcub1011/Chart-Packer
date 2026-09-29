using ATL;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Scanning;

/// <summary>
/// Measures a song's audio, for when its metadata has no <c>song_length</c>: the song is as long
/// as its longest audio file.
/// </summary>
/// <remarks>
/// Durations are read by <see href="https://github.com/Zeugma440/atldotnet">ATL.NET</see> (MIT
/// License; see <c>THIRD-PARTY-NOTICES.md</c>) from the files' headers, without decoding the audio.
/// Only files directly in the song folder count, with the audio extensions of the filename
/// registry (sng-spec §8).
/// </remarks>
public static class AudioLength
{
    static AudioLength()
    {
        // ATL writes the stack trace of every file it can't parse to the console by default.
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    /// <summary>Whether <paramref name="name"/>, a path relative to the song folder, is an audio file that counts.</summary>
    public static bool IsAudioFile(string name) => AudioStems.IsAudioFile(name);

    /// <summary>The longest of the folder's audio files, in milliseconds.</summary>
    /// <param name="folder">The song folder.</param>
    /// <param name="files">Its listing, from <see cref="SongFolderListing"/>.</param>
    /// <returns>The length, or <see langword="null"/> when no audio file's length could be read.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static long? LongestInFolder(string folder, IEnumerable<FolderFileEntry> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(files);

        long? longest = null;
        foreach (var file in files.Where(f => IsAudioFile(f.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(Path.Combine(folder, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                longest = Max(longest, Measure(stream, file.RelativePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable stem doesn't hide the others.
            }
        }

        return longest;
    }

    /// <summary>The longest of the audio files stored in a <c>.sng</c>, in milliseconds.</summary>
    /// <param name="sng">An open handle to the <c>.sng</c>.</param>
    /// <param name="read">What <see cref="SngReader"/> read from it.</param>
    /// <returns>The length, or <see langword="null"/> when no audio file's length could be read.</returns>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static long? LongestInSng(SafeFileHandle sng, SngReadResult read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sng);
        ArgumentNullException.ThrowIfNull(read);
        if (read.Header is not { } header)
            return null;

        long? longest = null;
        foreach (var file in read.Files.Where(f => IsAudioFile(f.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new SngEntryStream(sng, file, header.Mask.Span);
                longest = Max(longest, Measure(stream, file.Name));
            }
            catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
            {
                // A range past the end of the file, or one that doesn't fit: skip that file.
            }
        }

        return longest;
    }

    /// <summary>The stream's duration in whole milliseconds, or <see langword="null"/> when it isn't known.</summary>
    private static long? Measure(Stream stream, string name)
    {
        double ms;
        try
        {
            ms = new Track(stream, Path.GetExtension(name)).DurationMs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ATL reports damaged or unrecognised audio as assorted exceptions; any of them means
            // this file's length isn't known.
            return null;
        }

        return ms >= 1 && ms < long.MaxValue ? (long)ms : null;
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
