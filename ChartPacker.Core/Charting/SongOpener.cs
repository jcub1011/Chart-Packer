using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Charting;

/// <summary>Why a song couldn't be opened in Chart Builder.</summary>
public enum SongOpenProblemKind
{
    /// <summary>There's no song folder or <c>.sng</c> there.</summary>
    Missing,

    /// <summary>The song has neither <c>notes.mid</c> nor <c>notes.chart</c>.</summary>
    NoChart,

    /// <summary>The chart is larger than <see cref="ChartReader.MaxBytes"/>.</summary>
    ChartTooLarge,

    /// <summary>The song or its chart couldn't be read; the problem's detail says why.</summary>
    Unreadable,
}

/// <summary>Why a song couldn't be opened, and for <see cref="SongOpenProblemKind.Unreadable"/>, what went wrong.</summary>
public sealed record SongOpenProblem(SongOpenProblemKind Kind, string? Detail = null);

/// <summary>A song read for Chart Builder's editor (charter-spec C2): its metadata and files, and its chart in the editable model.</summary>
/// <param name="Pairs">The song's stored metadata, in order.</param>
/// <param name="StoredNames">The names of the files directly in the song.</param>
/// <param name="ChartName">The chart file read, as stored: <c>notes.mid</c>, or <c>notes.chart</c> when there's no <c>notes.mid</c>.</param>
/// <param name="Load">The chart, and what of it couldn't be taken in as written.</param>
/// <param name="Options">The settings the chart is read with: the song's, or for a <c>.chart</c>, the conversion's.</param>
/// <param name="Delay">How much later than its ticks say the chart plays against the audio (<see cref="SongChartSettings.DelayOf(IReadOnlyList{KeyValuePair{string, string}}, TimeSpan?)"/>).</param>
/// <param name="Import">For a <c>notes.chart</c>, what converting it gave: its <c>Offset</c> and warnings.</param>
public sealed record OpenedSong(
    SongEntry Entry,
    IReadOnlyList<KeyValuePair<string, string>> Pairs,
    IReadOnlyList<string> StoredNames,
    string ChartName,
    ChartLoadResult Load,
    ChartReadOptions Options,
    TimeSpan Delay,
    ChartImportResult? Import)
{
    /// <summary>The chart, in the editable model.</summary>
    public EditableChart Chart => Load.Chart;

    /// <summary>The <c>notes.chart</c>'s conversion warnings; none for a <c>notes.mid</c>.</summary>
    public IReadOnlyList<ChartMergeWarning> ImportWarnings => Import?.Warnings ?? [];

    /// <summary>The value of the metadata key <paramref name="key"/>, ignoring case; the last when there are several, as the games read it.</summary>
    public string? Value(string key)
    {
        string? value = null;
        foreach (var pair in Pairs)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && pair.Value.Trim().Length > 0)
                value = pair.Value.Trim();
        }

        return value;
    }

    /// <summary>"Artist – Name", or whichever of them it has, or else the song's file or folder name.</summary>
    public string Title => (Value("artist"), Value("name")) switch
    {
        ({ } artist, { } name) => $"{artist} – {name}",
        (null, { } name) => name,
        ({ } artist, null) => artist,
        _ => Entry.DisplayName,
    };
}

/// <summary>The song <see cref="SongOpener.OpenAsync"/> read, or why it couldn't.</summary>
public sealed record SongOpenResult(OpenedSong? Song, SongOpenProblem? Problem)
{
    public static SongOpenResult Failed(SongOpenProblemKind kind, string? detail = null) => new(null, new SongOpenProblem(kind, detail));
}

/// <summary>Opens a song for Chart Builder's editor (charter-spec C2).</summary>
public static class SongOpener
{
    /// <summary>
    /// The song at <paramref name="path"/>: a folder, a <c>.sng</c>, or any other file, which stands for the folder it's
    /// in (such as its <c>song.ini</c> or <c>notes.mid</c>). <see langword="null"/> when there's nothing there.
    /// </summary>
    public static SongEntry? EntryOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(full))
            return new SongEntry(SongEntryKind.SongFolder, full, ".", Path.GetFileName(full));
        if (!File.Exists(full))
            return null;
        if (Path.GetExtension(full).Equals(".sng", StringComparison.OrdinalIgnoreCase))
            return new SongEntry(SongEntryKind.SngFile, full, ".", Path.GetFileNameWithoutExtension(full));

        string? folder = Path.GetDirectoryName(full);
        return folder is null ? null : new SongEntry(SongEntryKind.SongFolder, folder, ".", Path.GetFileName(folder));
    }

    /// <summary>
    /// Reads <paramref name="entry"/>'s metadata and files, and its chart into the editable model: <c>notes.mid</c> as
    /// it is, or <c>notes.chart</c> converted (charter-spec C2 items 2 and 3). On the thread pool.
    /// </summary>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<SongOpenResult> OpenAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => OpenCoreAsync(entry, cancellationToken), cancellationToken);
    }

    private static async Task<SongOpenResult> OpenCoreAsync(SongEntry entry, CancellationToken cancellationToken)
    {
        bool exists = entry.Kind == SongEntryKind.SngFile ? File.Exists(entry.FullPath) : Directory.Exists(entry.FullPath);
        if (!exists)
            return SongOpenResult.Failed(SongOpenProblemKind.Missing);

        try
        {
            IReadOnlyList<string> stored = [.. SongFile.ListTopLevel(entry, cancellationToken).Select(f => f.Name)];
            var pairs = await ReadPairsAsync(entry, stored, cancellationToken).ConfigureAwait(false);
            if (ChartReader.Pick(stored) is not { } chartName)
                return SongOpenResult.Failed(SongOpenProblemKind.NoChart);

            byte[]? bytes = await SongFile.ReadAsync(entry, chartName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return SongOpenResult.Failed(SongOpenProblemKind.ChartTooLarge);

            var options = ChartReadOptions.From(pairs);
            if (chartName.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase))
            {
                var load = MidiChartLoader.Load(bytes, options);
                return new SongOpenResult(new OpenedSong(entry, pairs, stored, chartName, load, options, SongChartSettings.DelayOf(pairs, (TimeSpan?)null), null), null);
            }

            var import = MidiChartLoader.LoadChart(bytes, options);
            var song = new OpenedSong(entry, pairs, stored, chartName, import.Load, import.Options, SongChartSettings.DelayOf(pairs, import.Offset), import);
            return new SongOpenResult(song, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A malformed chart is a ChartFormatException, which is an IOException.
            return SongOpenResult.Failed(SongOpenProblemKind.Unreadable, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadPairsAsync(SongEntry entry, IReadOnlyList<string> stored, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
            return [.. SngReader.Read(entry.FullPath, cancellationToken).Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))];

        string? ini = stored.FirstOrDefault(n => n.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        return ini is null ? [] : (await SongIni.ReadAsync(Path.Combine(entry.FullPath, ini), cancellationToken).ConfigureAwait(false)).Pairs;
    }
}
