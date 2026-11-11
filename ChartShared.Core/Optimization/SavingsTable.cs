namespace ChartShared.Core.Optimization;

/// <summary>One codec's line in the Optimize window's savings table.</summary>
/// <param name="CodecKey">The input codec, as <c>video/vp9</c>.</param>
/// <param name="AssetsSampled">How many of its files were estimated.</param>
/// <param name="AssetsTotal">How many of its files the library has.</param>
/// <param name="TotalInputBytes">The size of all the library's files in the codec.</param>
/// <param name="EstimatedSavingBytes">What's expected to be saved across the library; negative when the codec would get larger.</param>
/// <param name="SavingPerChartBytes">The sampled files' saving averaged over the charts sampled; null when nothing was estimated.</param>
/// <param name="IsCompatibilityConversion">
/// The codec is one the games can't all play (<see cref="VideoCodecs.IsUnsupported"/>), so converting it is for
/// compatibility and its saving, usually negative, is a cost rather than a gain.
/// </param>
public sealed record SavingsRow(
    string CodecKey,
    int AssetsSampled,
    int AssetsTotal,
    long TotalInputBytes,
    long EstimatedSavingBytes,
    long? SavingPerChartBytes,
    bool IsCompatibilityConversion);

/// <summary>A <see cref="SavingsEstimate"/> as the rows the Optimize window shows: one per codec, and totals.</summary>
/// <param name="Rows">The codecs, largest library size first.</param>
/// <param name="TotalSavingBytes">What's expected to be saved across the rows, the cost of compatibility conversions included.</param>
/// <param name="CompatibilityCostBytes">How much the compatibility conversions are expected to add; 0 when there are none, or none grow.</param>
public sealed record SavingsTable(IReadOnlyList<SavingsRow> Rows, long TotalSavingBytes, long CompatibilityCostBytes)
{
    /// <summary>Shapes <paramref name="estimate"/> for display.</summary>
    /// <param name="includeCompatibility">
    /// Whether compatibility conversions count towards the total. Off, they're still listed, for what converting would
    /// cost, but the total is for what the run will do.
    /// </param>
    public static SavingsTable From(SavingsEstimate estimate, bool includeCompatibility)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        var rows = estimate.Codecs
            .Select(c => new SavingsRow(
                c.CodecKey,
                c.AssetsSampled,
                c.AssetsTotal,
                c.TotalInputBytes,
                c.HasEstimate ? c.EstimatedSavingBytes : 0,
                c.HasEstimate && estimate.ChartsSampled > 0 ? (c.SampledInputBytes - c.SampledOutputBytes) / estimate.ChartsSampled : null,
                VideoCodecs.IsUnsupported(c.CodecKey)))
            .OrderByDescending(r => r.TotalInputBytes)
            .ThenBy(r => r.CodecKey, StringComparer.Ordinal)
            .ToList();

        long total = rows.Where(r => includeCompatibility || !r.IsCompatibilityConversion).Sum(r => r.EstimatedSavingBytes);
        long cost = rows.Where(r => r.IsCompatibilityConversion && r.EstimatedSavingBytes < 0).Sum(r => -r.EstimatedSavingBytes);
        return new SavingsTable(rows, total, cost);
    }
}
