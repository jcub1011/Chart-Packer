using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Metadata;

/// <summary>Writes a song's <see cref="MetadataDraft"/>: its metadata, album art, file changes, chart vocals and chart offsets.</summary>
public static class DraftWriter
{
    /// <summary>
    /// Re-reads the <c>.sng</c> at <paramref name="sngPath"/> and rewrites it with the draft
    /// applied. Every key and file the draft doesn't touch is carried over as stored.
    /// </summary>
    /// <remarks>
    /// An album-art change removes every stored album-art file, and a background change every
    /// stored file of its kind. An added file whose name is already stored replaces it. A vocals
    /// change, then the chart offsets, are applied to the chart the song ends up with, added or stored.
    /// </remarks>
    /// <returns>The findings that stopped the write, or the rewritten file's findings.</returns>
    /// <exception cref="ArgumentException">An added file has the same name as the new album art.</exception>
    /// <exception cref="IOException">
    /// A file can't be read or written, or an added file changed size; or, as a
    /// <see cref="ChartFormatException"/>, the vocals or chart offsets can't be applied to the chart.
    /// </exception>
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

        foreach (var kind in SongBackgrounds.Kinds)
        {
            if (draft.GetBackground(kind) is not { } background)
                continue;

            remove.UnionWith(read.Files.Select(f => f.Name).Where(n => SongBackgrounds.IsName(n, kind)));
            if (background.SourcePath is { } source)
                add.Add(new SngSourceFile(BackgroundStoredName(kind, source), source, new FileInfo(source).Length));
        }

        var stored = read.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entry = new SongEntry(SongEntryKind.SngFile, sngPath, Path.GetFileName(sngPath), Path.GetFileName(sngPath));
        var (adds, shiftedChart) = await AddsWithShiftedChartAsync(draft, pairs, stored, entry, cancellationToken).ConfigureAwait(false);
        try
        {
            // RewriteAsync refuses an add that clashes with a kept file, so a replaced file is removed first.
            foreach (var file in adds)
            {
                if (stored.Contains(file.StoredName))
                    remove.Add(file.StoredName);
                add.Add(new SngSourceFile(file.StoredName, file.SourcePath, new FileInfo(file.SourcePath).Length));
            }

            return await SngWriter.RewriteAsync(sngPath, pairs, remove, add, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(shiftedChart);
        }
    }

    /// <summary>
    /// Applies the draft to the song folder <paramref name="folder"/>: its metadata to
    /// <paramref name="songIniPath"/>, then its album art and backgrounds, then its file changes.
    /// </summary>
    /// <remarks>A draft without metadata changes never creates a <c>song.ini</c>.</remarks>
    /// <exception cref="ArgumentException">A file change names the <c>song.ini</c> or a path outside the folder.</exception>
    /// <exception cref="IOException">
    /// A file can't be read, written or deleted; or, as a <see cref="ChartFormatException"/>, the
    /// vocals or chart offsets can't be applied to the chart, in which case nothing was written.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The write was cancelled.</exception>
    public static async Task SaveFolderAsync(string folder, string songIniPath, MetadataDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(songIniPath);
        ArgumentNullException.ThrowIfNull(draft);

        // The changed chart is worked out before anything is written, so a chart it can't be applied to changes nothing.
        IReadOnlyList<KeyValuePair<string, string>> pairs = [];
        IReadOnlySet<string> stored = new HashSet<string>();
        if (draft.HasChartEdits)
        {
            var read = File.Exists(songIniPath) ? await SongIni.ReadAsync(songIniPath, cancellationToken).ConfigureAwait(false) : null;
            pairs = draft.ApplyTo(read?.Pairs ?? []);
            stored = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var entry = new SongEntry(SongEntryKind.SongFolder, folder, Path.GetFileName(folder), Path.GetFileName(folder));
        var (adds, shiftedChart) = await AddsWithShiftedChartAsync(draft, pairs, stored, entry, cancellationToken).ConfigureAwait(false);
        try
        {
            if (draft.HasMetadataChanges)
                await SongIni.ApplyEditsAsync(songIniPath, draft, cancellationToken).ConfigureAwait(false);
            if (draft.AlbumArt is { } art)
                await AlbumArt.ApplyToFolderAsync(folder, art, cancellationToken).ConfigureAwait(false);
            foreach (var kind in SongBackgrounds.Kinds)
            {
                if (draft.GetBackground(kind) is not { } background)
                    continue;

                string? name = background.SourcePath is { } source ? BackgroundStoredName(kind, source) : null;
                await FileSlot.ApplyToFolderAsync(folder, n => SongBackgrounds.IsName(n, kind), name, background.SourcePath, cancellationToken).ConfigureAwait(false);
            }

            if (draft.RemovedFiles.Count > 0 || adds.Count > 0)
                await SongFolderFiles.ApplyAsync(folder, draft.RemovedFiles, adds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(shiftedChart);
        }
    }

    /// <summary>
    /// The draft's added files, plus, when it changes the chart's vocals or has chart offsets, the chart
    /// the song ends up with once they're applied: written to a temp file that replaces the chart's add
    /// or its stored file.
    /// </summary>
    /// <param name="pairs">The song's metadata once the draft is applied, for <c>delay</c> and <c>pro_drums</c>.</param>
    /// <param name="stored">The names of the song's stored files.</param>
    /// <returns>The adds, and the temp file to delete once they're written, if there is one.</returns>
    private static async Task<(IReadOnlyList<AddedFile> Adds, string? Temp)> AddsWithShiftedChartAsync(
        MetadataDraft draft, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlySet<string> stored, SongEntry entry, CancellationToken cancellationToken)
    {
        var adds = draft.AddedFiles.ToList();
        if (!draft.HasChartEdits)
            return (adds, null);

        var chart = DraftChart.Of(draft, stored)
            ?? throw new ChartFormatException("The song has no chart to change.");
        string name = chart.Name;
        byte[] bytes = await chart.ReadAsync(entry, cancellationToken).ConfigureAwait(false)
            ?? throw new ChartFormatException($"'{name}' is too large to change.");

        // The vocals first: they're placed by song time, and moving notes leaves the vocal tracks alone.
        var vocalsOffset = TimeSpan.FromMilliseconds(draft.VocalsOffset);
        if (draft.Vocals is { } vocals)
        {
            var source = vocals.SourcePath is { } path ? await VocalsSource.LoadAsync(path, cancellationToken).ConfigureAwait(false) : null;
            bytes = VocalsEditor.Apply(bytes, name, pairs, source?.Chart, vocalsOffset).Bytes;
        }
        else if (draft.HasVocalsOffset)
        {
            bytes = VocalsEditor.Shift(bytes, name, pairs, vocalsOffset).Bytes;
        }

        if (draft.HasChartOffsets)
            bytes = ChartShifter.Shift(bytes, name, draft.ChartOffsets, ChartReadOptions.From(pairs)).Bytes;

        string temp = Path.Combine(Path.GetTempPath(), $"chartpacker-{Guid.NewGuid():N}{Path.GetExtension(name)}");
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);

        var add = new AddedFile(name, temp, bytes.Length);
        int added = chart.StagedPath is null ? -1 : adds.FindLastIndex(f => ChartFiles.IsChartName(f.StoredName));
        if (added >= 0)
            adds[added] = add;
        else
            adds.Add(add);
        return (adds, temp);
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string BackgroundStoredName(BackgroundKind kind, string source) =>
        SongBackgrounds.StoredNameFor(kind, source)
        ?? throw new ArgumentException($"'{source}' can't be a background {kind.ToString().ToLowerInvariant()}.", nameof(source));
}
