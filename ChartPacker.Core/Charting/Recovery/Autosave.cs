using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting.Recovery;

/// <summary>What an autosave does when its time comes (charter-spec C15 item 6).</summary>
public enum AutosaveAction
{
    /// <summary>Nothing: nothing has changed since the last autosave, or there's nothing unsaved.</summary>
    Nothing,

    /// <summary>Write the chart to the recovery file.</summary>
    Write,

    /// <summary>Delete the recovery file: the chart has nothing unsaved, as undoing back to the save does.</summary>
    Delete,

    /// <summary>Try again shortly: a drag is mid-way, or a save is running.</summary>
    Later,
}

/// <summary>Autosaving the chart open to the recovery folder (charter-spec C15 item 6).</summary>
public static class Autosave
{
    /// <summary>
    /// What an autosave does now: write when the chart has unsaved edits made since the last autosave
    /// (<paramref name="version"/> isn't <paramref name="autosavedVersion"/>), delete the recovery file once nothing is
    /// unsaved, and wait while the chart is busy.
    /// </summary>
    /// <param name="isDirty">Whether the chart has edits that aren't saved.</param>
    /// <param name="hasRecoveryFile">Whether an autosave of it is in the recovery folder.</param>
    /// <param name="version">A count of the chart's edits, undos and redos so far.</param>
    /// <param name="autosavedVersion">That count when it was last autosaved.</param>
    /// <param name="isBusy">Whether a drag's preview is applied to the chart, or a save is running.</param>
    public static AutosaveAction Next(bool isDirty, bool hasRecoveryFile, int version, int autosavedVersion, bool isBusy)
    {
        if (!isDirty)
            return hasRecoveryFile ? AutosaveAction.Delete : AutosaveAction.Nothing;
        if (version == autosavedVersion && hasRecoveryFile)
            return AutosaveAction.Nothing;
        return isBusy ? AutosaveAction.Later : AutosaveAction.Write;
    }

    /// <summary>The manifest of an autosave of <paramref name="song"/> made at <paramref name="at"/>.</summary>
    /// <param name="title">What the Editor's title says, to list it by.</param>
    public static RecoveryManifest ManifestFor(EditorSong song, string title, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(title);
        return new RecoveryManifest(
            song.Entry?.FullPath,
            song.Entry?.Kind,
            song.Opened?.ChartFile,
            at,
            0,
            "",
            song.EffectivePairs(),
            [.. song.PendingChanges().Select(c => KeyValuePair.Create(c.Key, c.Value))],
            [.. song.Reverted],
            song.Values,
            song.IsNew ? [.. song.Tracks.Where(t => t.IsStaged).Select(t => new NewChartStem(t.StagedSourcePath!, t.Stem))] : [],
            title)
        {
            DelayMs = song.DelayMs,
            Ratings = [.. song.Ratings],
            Edits = [.. song.Edits],
            AlbumArt = song.AlbumArt,
        };
    }

    /// <summary>
    /// The song <paramref name="opened"/> with its chart recovered from <paramref name="file"/> (C2 item 7): the parts
    /// the chart on disk has still need no rating, and the pending changes the user reverted stay reverted.
    /// </summary>
    /// <exception cref="IOException">The recovered chart can't be read.</exception>
    public static EditorSong Recover(OpenedSong opened, RecoveryFile file)
    {
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(file);
        var onDisk = opened.HasChartFile ? opened.Chart.Parts.Select(p => p.Instrument).ToHashSet() : [];
        var song = EditorSong.Of(opened with { Load = RecoveryStore.ReadChart(file) }, onDisk);
        song.DelayMs = file.Manifest.DelayMs;
        song.SetRatings(file.Manifest.Ratings);
        song.SetEdits(file.Manifest.Edits, file.Manifest.AlbumArt);
        song.RevertAll(file.Manifest.Reverted);
        return song;
    }

    /// <summary>
    /// A new chart that was never saved, recovered from <paramref name="file"/>: its chart, its audio from where it was,
    /// and its name and artist.
    /// </summary>
    /// <exception cref="IOException">The recovered chart can't be read.</exception>
    public static EditorSong RecoverNew(RecoveryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tracks = file.Manifest.Stems
            .Select(s => new SongTrack(s.Stem + Path.GetExtension(s.SourcePath).ToLowerInvariant(), s.Stem, s.SourcePath))
            .ToList();
        var song = EditorSong.New(RecoveryStore.ReadChart(file).Chart, tracks);
        song.Values = file.Manifest.Values;
        song.DelayMs = file.Manifest.DelayMs;
        song.SetRatings(file.Manifest.Ratings);
        song.SetEdits(file.Manifest.Edits, file.Manifest.AlbumArt);
        song.RevertAll(file.Manifest.Reverted);
        return song;
    }
}
