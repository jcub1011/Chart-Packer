using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting.Saving;

/// <summary>
/// The Editor's copies of the song's files a save replaces or removes (charter-spec C1, Art and Tracks), kept for the
/// rest of the session so an undo of an edit made before the save can put the file back. One per Editor window, in a
/// temp folder of its own, deleted as the window closes.
/// </summary>
public sealed class MediaStash : IDisposable
{
    /// <summary>The temp folders' prefix, which <see cref="SweepStale"/> looks for.</summary>
    public const string Prefix = "chartbuilder-undo-";

    private const int BufferSize = 81920;

    private int _next;

    private MediaStash(string folder) => Folder = folder;

    /// <summary>The folder the copies are kept in.</summary>
    public string Folder { get; }

    /// <summary>A stash in a new temp folder.</summary>
    /// <exception cref="IOException">The folder can't be made.</exception>
    public static MediaStash Create() => new(Directory.CreateTempSubdirectory(Prefix).FullName);

    /// <summary>
    /// Copies the file <paramref name="song"/> has stored in each of <paramref name="slots"/> that has one, before a save
    /// replaces or removes it: from its folder, or out of its <c>.sng</c>. A slot with more than one file, a stem in two
    /// formats, keeps the one listed first.
    /// </summary>
    /// <returns>Each copy by slot, under the name it was stored as; none for a slot with nothing stored.</returns>
    /// <exception cref="IOException">A file can't be read or copied.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Keeping was cancelled.</exception>
    public async Task<IReadOnlyDictionary<MediaSlot, string>> KeepAsync(EditorSong song, IEnumerable<MediaSlot> slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(slots);
        var kept = new Dictionary<MediaSlot, string>();
        if (song.Entry is not { } entry)
            return kept;

        foreach (var slot in slots.Distinct())
        {
            if (FileOf(song, slot) is not { } name)
                continue;

            string folder = Path.Combine(Folder, (_next++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, Path.GetFileName(name));
            await using (var input = SongFile.OpenRead(entry, name, cancellationToken) ?? throw new FileNotFoundException($"'{name}' isn't in the song any more.", name))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            kept[slot] = target;
        }

        return kept;
    }

    /// <summary>Deletes the stash's folders older than <paramref name="age"/>, left by Editor windows that didn't close cleanly.</summary>
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

    // The stored file the slot plays or shows: the one the games would pick, or the first of a stem's.
    private static string? FileOf(EditorSong song, MediaSlot slot)
    {
        var files = song.StoredFilesOf(slot);
        return slot.Kind switch
        {
            MediaKind.AlbumArt => AlbumArt.Pick(files, n => n),
            MediaKind.Track => files.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(),
            _ => SongBackgrounds.Pick(files, n => n, slot.BackgroundKind!.Value),
        };
    }
}
