using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Metadata;

/// <summary>The chart a song ends up with once its draft is saved, and where it's read from.</summary>
/// <param name="Name">Its stored name, <c>notes.chart</c> or <c>notes.mid</c>.</param>
/// <param name="StagedPath">The file the draft adds it from, or <see langword="null"/> for the song's stored chart.</param>
public readonly record struct DraftChart(string Name, string? StagedPath)
{
    /// <summary>
    /// The chart <paramref name="draft"/> leaves the song with: the last one it stages, otherwise
    /// the one <see cref="ChartReader.Pick"/> picks from the stored files it doesn't remove.
    /// </summary>
    /// <param name="storedNames">The names of the song's stored files.</param>
    /// <returns>The chart, or <see langword="null"/> when the song will have none.</returns>
    public static DraftChart? Of(MetadataDraft draft, IEnumerable<string> storedNames)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(storedNames);

        var staged = draft.AddedFiles.LastOrDefault(f => ChartFiles.IsChartName(f.StoredName));
        if (staged.StoredName is not null)
            return new DraftChart(staged.StoredName, staged.SourcePath);

        return ChartReader.Pick(storedNames.Where(n => !draft.IsFileRemoved(n))) is { } name ? new DraftChart(name, null) : null;
    }

    /// <summary>Reads the chart, from the staged file or out of <paramref name="entry"/>.</summary>
    /// <returns>Its bytes, or <see langword="null"/> when it's gone or larger than <see cref="ChartReader.MaxBytes"/>.</returns>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public Task<byte[]?> ReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return StagedPath is { } path
            ? SongFile.ReadFileAsync(path, ChartReader.MaxBytes, cancellationToken)
            : SongFile.ReadAsync(entry, Name, ChartReader.MaxBytes, cancellationToken);
    }
}
