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
/// <param name="ChartFile">The chart file on disk as it was read, or null for a new chart in a song that had none.</param>
public sealed record OpenedSong(
    SongEntry Entry,
    IReadOnlyList<KeyValuePair<string, string>> Pairs,
    IReadOnlyList<string> StoredNames,
    string ChartName,
    ChartLoadResult Load,
    ChartReadOptions Options,
    TimeSpan Delay,
    ChartImportResult? Import,
    ChartFileStamp? ChartFile = null)
{
    /// <summary>Whether the song has a chart file on disk: false for a new chart in a song that had neither.</summary>
    public bool HasChartFile => ChartFile is not null;

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

/// <summary>
/// The file a song's chart was read from, as it was then, for telling whether it's changed since (charter-spec C15 item
/// 6): the chart file in a folder, or the <c>.sng</c> itself, as the files in a <c>.sng</c> have no times of their own.
/// </summary>
/// <param name="Name">The chart file's stored name.</param>
/// <param name="Length">The size of the file on disk, in bytes.</param>
/// <param name="LastWriteTimeUtc">When the file on disk was last written.</param>
public sealed record ChartFileStamp(string Name, long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>The stamp of <paramref name="chartName"/> in <paramref name="entry"/> as it is now, or null when it isn't there.</summary>
    public static ChartFileStamp? Of(SongEntry entry, string chartName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(chartName);
        try
        {
            var file = new FileInfo(entry.Kind == SongEntryKind.SngFile ? entry.FullPath : Path.Combine(entry.FullPath, chartName));
            return file.Exists ? new ChartFileStamp(chartName, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A song with neither chart file, which can be given a new chart (charter-spec C1, New chart in this song).</summary>
/// <param name="Pairs">The song's stored metadata, in order.</param>
/// <param name="StoredNames">The names of the files directly in the song.</param>
public sealed record NoChartSong(SongEntry Entry, IReadOnlyList<KeyValuePair<string, string>> Pairs, IReadOnlyList<string> StoredNames);

/// <summary>The song <see cref="SongOpener.OpenAsync"/> read, or why it couldn't.</summary>
/// <param name="Empty">For <see cref="SongOpenProblemKind.NoChart"/>, the song as read, for a new chart to be made in it.</param>
public sealed record SongOpenResult(OpenedSong? Song, SongOpenProblem? Problem, NoChartSong? Empty = null)
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

    /// <summary>
    /// <paramref name="song"/>, which has neither chart file, opened with <paramref name="chart"/> as a new chart in it
    /// (charter-spec C1, New chart in this song): its first save adds <c>notes.mid</c> and the <c>song.ini</c> keys.
    /// </summary>
    public static OpenedSong NewChartIn(NoChartSong song, EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(chart);
        return new OpenedSong(
            song.Entry,
            song.Pairs,
            song.StoredNames,
            ChartFiles.MidiName,
            new ChartLoadResult(chart, []),
            ChartReadOptions.From(song.Pairs),
            SongChartSettings.DelayOf(song.Pairs, (TimeSpan?)null),
            null);
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
                return new SongOpenResult(null, new SongOpenProblem(SongOpenProblemKind.NoChart), new NoChartSong(entry, pairs, stored));

            // Stamped before it's read, so a change made while it's read shows as a change.
            var stamp = ChartFileStamp.Of(entry, chartName);
            byte[]? bytes = await SongFile.ReadAsync(entry, chartName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return SongOpenResult.Failed(SongOpenProblemKind.ChartTooLarge);

            var chart = LoadChart(chartName, bytes, pairs);
            return new SongOpenResult(new OpenedSong(entry, pairs, stored, chartName, chart.Load, chart.Options, chart.Delay, chart.Import, stamp), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A malformed chart is a ChartFormatException, which is an IOException.
            return SongOpenResult.Failed(SongOpenProblemKind.Unreadable, ex.Message);
        }
    }

    /// <summary>
    /// The chart <paramref name="bytes"/> hold, read as the games read it with <paramref name="pairs"/>: <c>notes.mid</c>
    /// as it is, or <c>notes.chart</c> converted in memory. Both apps read charts this way: Chart Builder to edit one, Chart
    /// Packer to lint it (charter-spec C16 item 5).
    /// </summary>
    /// <param name="chartName">The chart file's name: <c>notes.mid</c> or <c>notes.chart</c>.</param>
    /// <exception cref="ChartFormatException">The chart is malformed.</exception>
    public static (ChartLoadResult Load, ChartReadOptions Options, TimeSpan Delay, ChartImportResult? Import) LoadChart(
        string chartName, byte[] bytes, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(chartName);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(pairs);
        var options = ChartReadOptions.From(pairs);
        if (chartName.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase))
            return (MidiChartLoader.Load(bytes, options), options, SongChartSettings.DelayOf(pairs, (TimeSpan?)null), null);

        var import = MidiChartLoader.LoadChart(bytes, options);
        return (import.Load, import.Options, SongChartSettings.DelayOf(pairs, import.Offset), import);
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadPairsAsync(SongEntry entry, IReadOnlyList<string> stored, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
            return [.. SngReader.Read(entry.FullPath, cancellationToken).Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))];

        string? ini = stored.FirstOrDefault(n => n.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        return ini is null ? [] : (await SongIni.ReadAsync(Path.Combine(entry.FullPath, ini), cancellationToken).ConfigureAwait(false)).Pairs;
    }
}
