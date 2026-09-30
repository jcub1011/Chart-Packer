using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Merging;

/// <summary>A chart to take vocals from, for <see cref="VocalsEditor"/>, as it was read.</summary>
/// <remarks>
/// A draft keeps the source it was given, and saving writes these vocals rather than reading the
/// file again, so what's saved is what was shown even when the file has changed since.
/// </remarks>
/// <param name="FilePath">The chart or <c>.sng</c> it was read from.</param>
/// <param name="Chart">Its chart as a MIDI file, placed against its song's audio.</param>
/// <param name="Vocals">Its vocal tracks.</param>
public sealed record VocalsSource(string FilePath, MidiChart Chart, VocalChart Vocals)
{
    /// <summary>
    /// Reads the vocals of <paramref name="path"/>: a <c>.sng</c>, whose chart and metadata it holds,
    /// or a <c>.chart</c> or MIDI file, with the <c>song.ini</c> beside it when there is one.
    /// </summary>
    /// <remarks>The song's <c>delay</c> places the chart, so its vocals land where they're sung in the song they came from.</remarks>
    /// <exception cref="IOException">
    /// The file can't be read, or isn't a chart or a <c>.sng</c> with one; or, as a
    /// <see cref="ChartFormatException"/>, its chart is malformed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static async Task<VocalsSource> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IReadOnlyList<KeyValuePair<string, string>> pairs;
        string chartName;
        byte[]? bytes;
        if (Path.GetExtension(path).Equals(".sng", StringComparison.OrdinalIgnoreCase))
        {
            var read = await SngReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (read.HasErrors)
                throw new IOException($"'{Path.GetFileName(path)}' can't be read.");

            chartName = ChartReader.Pick(read.Files.Select(f => f.Name))
                ?? throw new ChartFormatException($"'{Path.GetFileName(path)}' has no chart.");
            pairs = [.. read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))];
            var entry = new SongEntry(SongEntryKind.SngFile, path, Path.GetFileName(path), Path.GetFileName(path));
            bytes = await SongFile.ReadAsync(entry, chartName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            chartName = ChartFiles.StoredNameFor(path)
                ?? throw new IOException($"'{Path.GetFileName(path)}' isn't a chart.");
            pairs = await SiblingIniAsync(path, cancellationToken).ConfigureAwait(false);
            bytes = await SongFile.ReadFileAsync(path, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null)
            throw new ChartFormatException($"'{Path.GetFileName(path)}' is too large to read.");

        var (chart, _) = MidiChart.Load(bytes, chartName, pairs);
        return new VocalsSource(path, chart, VocalReader.Read(chart.Midi, ChartFiles.MidiName));
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> SiblingIniAsync(string chartPath, CancellationToken cancellationToken)
    {
        string? folder = Path.GetDirectoryName(chartPath);
        string? ini = folder is null ? null : Directory.EnumerateFiles(folder)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        return ini is null ? [] : (await SongIni.ReadAsync(ini, cancellationToken).ConfigureAwait(false)).Pairs;
    }
}
