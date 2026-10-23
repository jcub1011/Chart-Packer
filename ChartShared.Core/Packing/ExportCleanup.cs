using System.Globalization;
using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Library;
using ChartShared.Core.Merging;
using ChartShared.Core.Resources;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Packing;

/// <summary>
/// What a packed <c>.sng</c> leaves out, as no game needs it: the <c>song.ini</c> keys of parts the chart has no notes
/// for (SNG062), and the events only Chart Builder reads from <c>notes.mid</c>, its tempo anchors (charter-spec §7.3).
/// </summary>
/// <remarks>
/// Only packing and exporting a <c>.sng</c> leave them out. Saving a song keeps them, so a song edited in a <c>.sng</c>
/// keeps its anchors.
/// </remarks>
public static class ExportCleanup
{
    /// <summary>
    /// The known parts <paramref name="chart"/>, a chart stored as <paramref name="storedName"/>, has a note in, by
    /// <see cref="MidiPartCatalog"/> id; <see langword="null"/> when it's malformed.
    /// </summary>
    internal static IReadOnlySet<string>? PartsOf(byte[] chart, string storedName, IReadOnlyList<KeyValuePair<string, string>> metadata) =>
        ChartContents.PartIdsOf(chart, storedName, ChartReadOptions.From(metadata));

    /// <summary>
    /// <paramref name="metadata"/> without the keys of the parts the chart has no notes in, each left out with an Info
    /// finding added to <paramref name="findings"/>. A <c>.chart</c> is only known not to have the parts its reader reads,
    /// so the keys of the others, such as vocals', are kept. With no <paramref name="parts"/>, as when there's no chart or
    /// it can't be read, every key is kept.
    /// </summary>
    /// <param name="parts">The parts the chart has, as <see cref="PartsOf"/> gives them.</param>
    /// <param name="isMidi">Whether the chart is a MIDI file.</param>
    internal static IReadOnlyList<KeyValuePair<string, string>> PruneMetadata(
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        IReadOnlySet<string>? parts,
        bool isMidi,
        List<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(findings);
        if (parts is null)
            return metadata;

        var kept = new List<KeyValuePair<string, string>>(metadata.Count);
        foreach (var pair in metadata)
        {
            if (MidiPartCatalog.OwnerOf(pair.Key) is { } owner
                && !parts.Contains(owner.Id)
                && (isMidi || MidiPartCatalog.InstrumentsOf(owner).Count > 0))
            {
                findings.Add(new Finding(
                    SngRules.Sng062,
                    Severity.Info,
                    string.Format(CultureInfo.CurrentCulture, FindingMessages.PartKeyDroppedFormat, pair.Key)));
                continue;
            }

            kept.Add(pair);
        }

        return kept;
    }

    /// <summary>
    /// The MIDI file <paramref name="midi"/> without its tempo anchors, the tempo track's other events as they were and
    /// every other chunk byte for byte; <see langword="null"/> when it has none.
    /// </summary>
    /// <exception cref="ChartFormatException">The file isn't one <see cref="MidiChartReader"/> can read.</exception>
    internal static byte[]? StripEditorEvents(byte[] midi)
    {
        ArgumentNullException.ThrowIfNull(midi);
        var (_, chunks) = MidiChartReader.ChunksOf(midi);
        var tempo = chunks[0];
        var events = MidiFile.ReadEvents(midi, tempo);
        var kept = events.Where(e => !(MidiFile.TextOf(midi, e, TextEvent.TextType) is { } text && MidiChartLoader.AnchorOf(text) is not null)).ToList();
        if (kept.Count == events.Count)
            return null;

        using var output = new MemoryStream(midi.Length);
        output.Write(midi, 0, tempo.Start);
        MidiWriter.WriteTrack(output, midi, kept);
        output.Write(midi, tempo.End, midi.Length - tempo.End);
        return output.ToArray();
    }

    /// <summary>
    /// <paramref name="midi"/> as <see cref="StripEditorEvents"/> leaves it; <see langword="null"/> when it has nothing to
    /// leave out, or can't be read, so it's packed as it is.
    /// </summary>
    internal static byte[]? TryStripEditorEvents(byte[] midi)
    {
        try
        {
            return StripEditorEvents(midi);
        }
        catch (ChartFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the <c>.sng</c> at <paramref name="sourcePath"/> to <paramref name="targetPath"/>, replacing it, without
    /// what a packed <c>.sng</c> leaves out (see <see cref="ExportCleanup"/>). The source is left alone. A source that
    /// can't be read is copied as it is.
    /// </summary>
    /// <exception cref="IOException">A file can't be read or written, or the copy failed to verify.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The copy was cancelled; the target is left as it was.</exception>
    public static async Task CopySngAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var source = SngReader.Read(sourcePath, cancellationToken);
        var metadata = source.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();
        IReadOnlyList<KeyValuePair<string, string>> pruned = metadata;
        byte[]? stripped = null;
        SngFileEntry? chartEntry = null;
        if (!source.HasErrors
            && source.Header is { } header
            && ChartReader.Pick(source.Files.Select(f => f.Name)) is { } chartName)
        {
            chartEntry = source.Files.First(f => f.Name == chartName);
            if (SongFile.ReadFromSng(sourcePath, header, chartEntry, ChartReader.MaxBytes, cancellationToken) is { } chart)
            {
                bool isMidi = ChartReader.IsMidi(chartName);
                pruned = PruneMetadata(metadata, PartsOf(chart, chartName, metadata), isMidi, []);
                stripped = isMidi ? TryStripEditorEvents(chart) : null;
            }
        }

        // Made beside the target and moved into place once it's done, so a target that's there is only ever replaced
        // whole, once another that has it open lets go of it.
        string copy = StagingPath.For(targetPath);
        DirectoryInfo? staging = null;
        try
        {
            File.Copy(sourcePath, copy);
            if (pruned.Count != metadata.Count || stripped is not null)
            {
                IReadOnlyList<SngSourceFile> add = [];
                if (stripped is not null)
                {
                    staging = Directory.CreateTempSubdirectory("chartpacker-export-");
                    string staged = Path.Combine(staging.FullName, chartEntry!.Name);
                    await File.WriteAllBytesAsync(staged, stripped, cancellationToken).ConfigureAwait(false);
                    add = [new SngSourceFile(chartEntry.Name, staged, stripped.Length)];
                }

                var findings = await SngWriter.RewriteAsync(copy, pruned, stripped is null ? [] : [chartEntry!.Name], add, cancellationToken).ConfigureAwait(false);
                if (findings.FirstOrDefault(f => f.Severity == Severity.Error) is { } error)
                    throw new IOException(error.Message);
            }

            await SongFileCommit.MoveOverAsync(copy, targetPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            StagingPath.TryDeleteFile(copy);
            throw;
        }
        finally
        {
            if (staging is not null)
                TryDeleteFolder(staging.FullName);
        }
    }

    /// <summary>
    /// <paramref name="files"/> with the MIDI chart among them replaced by a copy without its tempo anchors, written in a
    /// new temp folder, <c>Staging</c>, for the caller to remove; <paramref name="files"/> itself, and no folder, when it
    /// has none.
    /// </summary>
    internal static async Task<(IReadOnlyList<SngSourceFile> Files, string? Staging)> StripChartAsync(IReadOnlyList<SngSourceFile> files, CancellationToken cancellationToken)
    {
        int index = -1;
        for (int i = 0; i < files.Count; i++)
        {
            if (files[i].StoredName.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase))
                index = i;
        }

        if (index < 0 || files[index].Length > ChartReader.MaxBytes)
            return (files, null);

        var chart = files[index];
        byte[] bytes = await File.ReadAllBytesAsync(chart.SourcePath, cancellationToken).ConfigureAwait(false);
        if (TryStripEditorEvents(bytes) is not { } stripped)
            return (files, null);

        var folder = Directory.CreateTempSubdirectory("chartpacker-pack-");
        string path = Path.Combine(folder.FullName, ChartFiles.MidiName);
        await File.WriteAllBytesAsync(path, stripped, cancellationToken).ConfigureAwait(false);
        var result = files.ToList();
        result[index] = chart with { SourcePath = path, Length = stripped.Length };
        return (result, folder.FullName);
    }

    internal static void TryDeleteFolder(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the OS to clear from the temp folder.
        }
    }
}
