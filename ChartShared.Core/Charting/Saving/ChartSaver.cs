using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Saving;

/// <summary>What a save writes (charter-spec C17 item 1).</summary>
/// <param name="Snapshot">The chart, as a <see cref="EditableChart.Clone"/> taken when the save started.</param>
/// <param name="After">The song's settings once the save's <c>song.ini</c> changes are made, which the read-back check reads the chart with.</param>
/// <param name="SongIniChanges">The pending <c>song.ini</c> changes the save makes: those the user hasn't reverted.</param>
/// <param name="ChartName">The name <c>notes.mid</c> is stored under: the song's own <c>notes.mid</c>'s, or <c>notes.mid</c>.</param>
/// <param name="RemoveChart">An imported <c>notes.chart</c>'s stored name, when the save removes it; null otherwise.</param>
/// <param name="Media">The files the save puts in place of the song's, or removes, by slot: art, backgrounds and tracks.</param>
/// <param name="StoredNames">The song's files as it was opened or last saved, which a track's change replaces those of its stem among.</param>
public sealed record ChartSaveRequest(
    EditableChart Snapshot,
    ChartReadOptions After,
    IReadOnlyList<PendingSongIniChange> SongIniChanges,
    string ChartName = ChartFiles.MidiName,
    string? RemoveChart = null,
    IReadOnlyDictionary<MediaSlot, MediaChange>? Media = null,
    IReadOnlyList<string>? StoredNames = null)
{
    /// <summary>The album art the save puts in place of the song's, or removes; null to leave it as it is.</summary>
    public AlbumArtChange? AlbumArt => MediaChangeOf(MediaSlot.AlbumArt) is { } change ? new AlbumArtChange(change.SourcePath) : null;

    /// <summary>The change the save makes to <paramref name="slot"/>; null to leave it as it is.</summary>
    public MediaChange? MediaChangeOf(MediaSlot slot) => Media is not null && Media.TryGetValue(slot, out var change) ? change : null;
}

/// <summary>Where a new chart's first save makes its song folder (charter-spec C1, The first save).</summary>
public sealed record NewSongTarget(string ParentFolder, string FolderName)
{
    /// <summary>The song folder's full path.</summary>
    public string FullPath => Path.Combine(Path.GetFullPath(ParentFolder), FolderName);
}

/// <summary>Saves a chart and its pending <c>song.ini</c> changes to a song (charter-spec C17, §4.6).</summary>
/// <remarks>
/// A save first writes the chart in memory and reads it back both ways (<see cref="Check"/>), and a difference stops
/// it. Only then is anything written: through the song's draft for a song that has a folder or <c>.sng</c>, so a
/// <c>.sng</c> keeps its mask and a folder's files are each replaced whole; or, for a new chart, as a new song folder
/// built beside its target and moved into place. Everything here runs off the editing thread, on the snapshot.
/// </remarks>
public static class ChartSaver
{
    /// <summary>
    /// What saving <paramref name="snapshot"/>, a clone of <paramref name="song"/>'s chart, writes: with
    /// <paramref name="changes"/> made to <c>song.ini</c>, which the read-back check reads the chart as, and the chart
    /// stored under the song's own <c>notes.mid</c>'s name, and the song's changes to its art, backgrounds and tracks.
    /// </summary>
    /// <param name="removeChart">An imported <c>notes.chart</c>'s stored name, when the save removes it.</param>
    public static ChartSaveRequest RequestFor(EditorSong song, EditableChart snapshot, IReadOnlyList<PendingSongIniChange> changes, string? removeChart = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        var after = MidiChartWriter.SettingsAfterSave(snapshot, ChartReadOptions.From(PendingSongIniChanges.Apply(song.Pairs, changes)));
        string chartName = song.Opened is { ChartName: var name } && name.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase)
            ? name
            : ChartFiles.MidiName;
        return new ChartSaveRequest(
            snapshot,
            after,
            changes,
            chartName,
            removeChart,
            new Dictionary<MediaSlot, MediaChange>(song.Media),
            song.Opened?.StoredNames ?? []);
    }

    /// <summary>
    /// <paramref name="song"/> as <paramref name="request"/> left it in <paramref name="entry"/>, for the editor to go on
    /// with (<see cref="EditorSong.Saved"/>): its <c>song.ini</c> with the changes made, its files with the chart's, and
    /// the chart file stamped as it is now.
    /// </summary>
    public static OpenedSong SavedSong(EditorSong song, SongEntry entry, ChartSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);
        var pairs = PendingSongIniChanges.Apply(song.Pairs, request.SongIniChanges);
        var media = request.Media ?? new Dictionary<MediaSlot, MediaChange>();
        var stored = (song.Opened?.StoredNames ?? StoredNamesOf(entry))
            .Where(n => request.RemoveChart is null || !n.Equals(request.RemoveChart, StringComparison.OrdinalIgnoreCase))
            .Where(n => !media.Keys.Any(slot => slot.Holds(n)))
            .ToList();
        if (!stored.Contains(request.ChartName, StringComparer.OrdinalIgnoreCase))
            stored.Add(request.ChartName);
        foreach (var (slot, change) in media)
        {
            if (change.SourcePath is { } source && slot.StoredNameFor(source) is { } name && !stored.Contains(name, StringComparer.OrdinalIgnoreCase))
                stored.Add(name);
        }

        return new OpenedSong(
            entry,
            pairs,
            stored,
            request.ChartName,
            new ChartLoadResult(song.Chart, []),
            ChartReadOptions.From(pairs),
            SongChartSettings.DelayOf(pairs, (TimeSpan?)null),
            null,
            ChartFileStamp.Of(entry, request.ChartName));
    }

    /// <summary>Writes the chart in memory and reads it back both ways (§4.3). Nothing is written to disk.</summary>
    /// <exception cref="ChartFormatException">The chart can't be written as MIDI at all.</exception>
    public static ChartWriteResult Check(ChartSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MidiChartWriter.WriteChecked(request.Snapshot, request.After);
    }

    /// <summary>
    /// The draft that saves <paramref name="request"/>: the chart, staged at <paramref name="stagedChart"/>, replacing
    /// the song's; the imported <c>notes.chart</c> removed, if it's to be; the album art, backgrounds and tracks; and the
    /// <c>song.ini</c> changes. A track put in place of its stem's replaces every file of the stem, in any format.
    /// </summary>
    public static MetadataDraft DraftFor(ChartSaveRequest request, string stagedChart, long length)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(stagedChart);
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile(request.ChartName, stagedChart, length));
        if (request.RemoveChart is { } removed)
            draft.RemoveFile(removed);
        foreach (var (slot, change) in request.Media ?? new Dictionary<MediaSlot, MediaChange>())
            AddMediaChange(draft, slot, change, request.StoredNames ?? []);
        foreach (var change in request.SongIniChanges)
        {
            if (change.Value is { } value)
                draft.Set(change.Key, value);
            else
                draft.Remove(change.Key);
        }

        return draft;
    }

    /// <summary>
    /// Saves <paramref name="written"/>, which <see cref="Check"/> found valid, and the request's <c>song.ini</c>
    /// changes to <paramref name="entry"/> through its draft (§4.6).
    /// </summary>
    /// <returns>The findings that stopped a <c>.sng</c>'s write, or the rewritten <c>.sng</c>'s; none for a folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="written"/> didn't pass the read-back check.</exception>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The save was cancelled.</exception>
    public static async Task<IReadOnlyList<Finding>> SaveAsync(SongEntry entry, ChartSaveRequest request, ChartWriteResult written, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);
        RequireValid(written);

        var temp = Directory.CreateTempSubdirectory("chartbuilder-save-");
        try
        {
            string staged = Path.Combine(temp.FullName, ChartFiles.MidiName);
            await File.WriteAllBytesAsync(staged, written.Bytes, cancellationToken).ConfigureAwait(false);
            var draft = DraftFor(request, staged, written.Bytes.Length);
            return await DraftWriter.SaveAsync(entry, draft, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StagingPath.TryDeleteDirectory(temp.FullName);
        }
    }

    /// <summary>
    /// Makes a new chart's song folder at <paramref name="target"/> (charter-spec C1, The first save), holding
    /// <paramref name="written"/> as <c>notes.mid</c>, the request's <c>song.ini</c> changes as its <c>song.ini</c>, and
    /// <paramref name="stems"/>. A failure or cancel leaves nothing behind, and an existing folder is never used.
    /// </summary>
    /// <returns>The new song folder's full path.</returns>
    /// <exception cref="ArgumentException"><paramref name="written"/> didn't pass the read-back check, or the folder name or a stem isn't valid.</exception>
    /// <exception cref="DirectoryNotFoundException">The parent folder doesn't exist.</exception>
    /// <exception cref="IOException">The target already exists, or a file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The save was cancelled.</exception>
    public static Task<string> SaveNewAsync(NewSongTarget target, ChartSaveRequest request, ChartWriteResult written, IReadOnlyList<NewChartStem> stems, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stems);
        RequireValid(written);

        return NewChartCreator.CreateAsync(
            new NewSongFolder
            {
                ParentFolder = target.ParentFolder,
                FolderName = target.FolderName,
                NotesMid = written.Bytes,
                SongIni = PendingSongIniChanges.Apply([], request.SongIniChanges),
                Stems = stems,
                AlbumArtPath = request.AlbumArt?.SourcePath,
                BackgroundImagePath = request.MediaChangeOf(MediaSlot.BackgroundImage)?.SourcePath,
                BackgroundVideoPath = request.MediaChangeOf(MediaSlot.BackgroundVideo)?.SourcePath,
            },
            cancellationToken);
    }

    /// <summary>
    /// Writes a copy of <paramref name="entry"/>, with <paramref name="written"/> and the request's changes applied, as the
    /// new folder <paramref name="targetFolder"/> (C17 item 4). The song itself is left as it is.
    /// </summary>
    /// <returns>Whether the copy was written, and the findings of extracting a <c>.sng</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="written"/> didn't pass the read-back check.</exception>
    /// <exception cref="IOException"><paramref name="targetFolder"/> exists, or a file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The export was cancelled.</exception>
    public static async Task<UnpackResult> ExportAsync(SongEntry entry, ChartSaveRequest request, ChartWriteResult written, string targetFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);
        RequireValid(written);

        var temp = Directory.CreateTempSubdirectory("chartbuilder-export-");
        try
        {
            string staged = Path.Combine(temp.FullName, ChartFiles.MidiName);
            await File.WriteAllBytesAsync(staged, written.Bytes, cancellationToken).ConfigureAwait(false);
            var draft = DraftFor(request, staged, written.Bytes.Length);
            return await SongExport.ExportFolderAsync(entry, draft, targetFolder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StagingPath.TryDeleteDirectory(temp.FullName);
        }
    }

    // A slot's change, as the draft makes it: the art and backgrounds each replace the file of their kind, in any format,
    // as the draft writer does; a track replaces its stem's files, as Chart Packer's Add track does.
    private static void AddMediaChange(MetadataDraft draft, MediaSlot slot, MediaChange change, IReadOnlyList<string> storedNames)
    {
        switch (slot.Kind)
        {
            case MediaKind.AlbumArt when change.SourcePath is { } source:
                draft.SetAlbumArt(source);
                break;
            case MediaKind.AlbumArt:
                draft.RemoveAlbumArt();
                break;
            case MediaKind.BackgroundImage or MediaKind.BackgroundVideo when change.SourcePath is { } source:
                draft.SetBackground(slot.BackgroundKind!.Value, source);
                break;
            case MediaKind.BackgroundImage or MediaKind.BackgroundVideo:
                draft.RemoveBackground(slot.BackgroundKind!.Value);
                break;
            case MediaKind.Track:
                var files = SongTracks.FilesOfStem(storedNames, slot.Stem!);
                if (change.SourcePath is { } path && slot.StoredNameFor(path) is { } name)
                {
                    // A stored file that differs only in case is still replaced, on a case-sensitive file system too.
                    draft.AddFile(new AddedFile(name, path, LengthOf(path)), files.Where(f => !f.Equals(name, StringComparison.Ordinal)));
                }
                else
                {
                    foreach (string file in files)
                        draft.RemoveFile(file);
                }

                break;
        }
    }

    // For display only: saving reads the size afresh.
    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> StoredNamesOf(SongEntry entry)
    {
        try
        {
            return [.. SongFile.ListTopLevel(entry).Select(f => f.Name)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void RequireValid(ChartWriteResult written)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (!written.IsValid)
            throw new ArgumentException("The chart didn't read back as it was written, so it isn't saved.", nameof(written));
    }
}
