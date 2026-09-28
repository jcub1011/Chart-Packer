using ChartPacker.Core.Ini;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Metadata;

/// <summary>Writes a song's <see cref="MetadataDraft"/>: its metadata, album art and file changes.</summary>
public static class DraftWriter
{
    /// <summary>
    /// Re-reads the <c>.sng</c> at <paramref name="sngPath"/> and rewrites it with the draft
    /// applied. Every key and file the draft doesn't touch is carried over as stored.
    /// </summary>
    /// <remarks>
    /// An album-art change removes every stored album-art file. An added file whose name is
    /// already stored replaces it.
    /// </remarks>
    /// <returns>The findings that stopped the write, or the rewritten file's findings.</returns>
    /// <exception cref="ArgumentException">An added file has the same name as the new album art.</exception>
    /// <exception cref="IOException">A file can't be read or written, or an added file changed size.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to a file is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task<IReadOnlyList<Finding>> SaveSngAsync(string sngPath, MetadataDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sngPath);
        ArgumentNullException.ThrowIfNull(draft);

        var read = await SngReader.ReadAsync(sngPath, cancellationToken).ConfigureAwait(false);
        if (read.HasErrors)
            return read.Findings;

        var pairs = draft.ApplyTo(read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList());
        var remove = new HashSet<string>(draft.RemovedFiles, StringComparer.OrdinalIgnoreCase);
        List<SngSourceFile> add = [];

        if (draft.AlbumArt is { } art)
        {
            remove.UnionWith(read.Files.Select(f => f.Name).Where(AlbumArt.IsAlbumArtName));
            if (art.SourcePath is { } source)
                add.Add(new SngSourceFile(AlbumArt.StoredNameFor(source)!, source, new FileInfo(source).Length));
        }

        // RewriteAsync refuses an add that clashes with a kept file, so a replaced file is removed first.
        var stored = read.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in draft.AddedFiles)
        {
            if (stored.Contains(file.StoredName))
                remove.Add(file.StoredName);
            add.Add(new SngSourceFile(file.StoredName, file.SourcePath, new FileInfo(file.SourcePath).Length));
        }

        return await SngWriter.RewriteAsync(sngPath, pairs, remove, add, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the draft to the song folder <paramref name="folder"/>: its metadata to
    /// <paramref name="songIniPath"/>, then its album art, then its file changes.
    /// </summary>
    /// <remarks>A draft without metadata changes never creates a <c>song.ini</c>.</remarks>
    /// <exception cref="ArgumentException">A file change names the <c>song.ini</c> or a path outside the folder.</exception>
    /// <exception cref="IOException">A file can't be read, written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task SaveFolderAsync(string folder, string songIniPath, MetadataDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(songIniPath);
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.HasMetadataChanges)
            await SongIni.ApplyEditsAsync(songIniPath, draft, cancellationToken).ConfigureAwait(false);
        if (draft.AlbumArt is { } art)
            await AlbumArt.ApplyToFolderAsync(folder, art, cancellationToken).ConfigureAwait(false);
        if (draft.HasFileChanges)
            await SongFolderFiles.ApplyAsync(folder, draft.RemovedFiles, draft.AddedFiles, cancellationToken).ConfigureAwait(false);
    }
}
