using System.Globalization;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Charting.Saving;

/// <summary>
/// Where Chart Packer keeps the charts its quick fixes make (<see cref="Validation.SongDifficultyGeneration"/>) until the
/// songs' drafts are saved or let go of: a temp folder of its own, deleted as the app closes. A song's draft names the
/// chart kept here as the file it adds, and saving copies it into the song.
/// </summary>
public sealed class GeneratedChartStash : IDisposable
{
    /// <summary>The temp folders' prefix, which <see cref="SweepStale"/> looks for.</summary>
    public const string Prefix = "chartpacker-generated-";

    private int _next;

    private GeneratedChartStash(string folder) => Folder = folder;

    /// <summary>The folder the charts are kept in.</summary>
    public string Folder { get; }

    /// <summary>A stash in a new temp folder.</summary>
    /// <exception cref="IOException">The folder can't be made.</exception>
    public static GeneratedChartStash Create() => new(Directory.CreateTempSubdirectory(Prefix).FullName);

    /// <summary>Keeps <paramref name="bytes"/> as a <c>notes.mid</c> of its own folder in the stash. Safe to call from many threads.</summary>
    /// <returns>The file's full path.</returns>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Keeping was cancelled.</exception>
    public async Task<string> KeepAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string folder = Path.Combine(Folder, Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, ChartFiles.MidiName);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Deletes the stash's folders older than <paramref name="age"/>, left by an app that didn't close cleanly.</summary>
    public static void SweepStale(TimeSpan age)
    {
        try
        {
            var cutoff = DateTime.UtcNow - age;
            foreach (var folder in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(Prefix + "*"))
            {
                if (folder.LastWriteTimeUtc < cutoff)
                    StagingPath.TryDeleteDirectory(folder.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next sweep.
        }
    }

    public void Dispose() => StagingPath.TryDeleteDirectory(Folder);
}
