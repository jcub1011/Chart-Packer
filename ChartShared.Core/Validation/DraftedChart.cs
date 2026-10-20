using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Validation;

/// <summary>
/// The chart a song plays once its draft is saved: one it stores, unless the draft removes it, or one the draft adds, read
/// from where it's staged.
/// </summary>
/// <param name="StoredName">The name the chart is, or will be, stored under.</param>
/// <param name="StagedPath">Where the draft has the chart staged; null for one the song stores.</param>
public sealed record DraftedChart(string StoredName, string? StagedPath)
{
    /// <summary>
    /// The chart a song with <paramref name="storedNames"/> plays once <paramref name="draft"/> is saved, picked as the
    /// games pick (<see cref="ChartReader.Pick"/>); null when it has none.
    /// </summary>
    public static DraftedChart? Of(IEnumerable<string> storedNames, MetadataDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(storedNames);
        var added = draft?.AddedFiles.Where(static f => ChartFiles.IsChartName(f.StoredName)).ToList() ?? [];
        var names = storedNames
            .Where(n => draft?.IsFileRemoved(n) != true && !added.Any(f => f.StoredName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Concat(added.Select(static f => f.StoredName));
        if (ChartReader.Pick(names) is not { } name)
            return null;
        var staged = added.FirstOrDefault(f => f.StoredName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return new DraftedChart(name, staged.StoredName is null ? null : staged.SourcePath);
    }

    /// <summary>The chart's bytes, from the song through <paramref name="files"/> or from where it's staged; null when it's larger than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="IOException">The chart can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public async Task<byte[]?> ReadAsync(SongFileReader files, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (StagedPath is null)
            return await files.ReadAsync(StoredName, maxBytes, cancellationToken).ConfigureAwait(false);
        if (new FileInfo(StagedPath).Length > maxBytes)
            return null;
        return await File.ReadAllBytesAsync(StagedPath, cancellationToken).ConfigureAwait(false);
    }
}
