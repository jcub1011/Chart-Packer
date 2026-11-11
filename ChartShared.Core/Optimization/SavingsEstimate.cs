using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>Which charts a savings estimate is made from. The fewer, the faster; the more, the more accurate.</summary>
public enum SamplingMode
{
    /// <summary>One chart the user picks. The fastest. Not offered for images.</summary>
    SingleChart,

    /// <summary>A sample of charts that's the same every time it's asked for: it's chosen from their relative paths.</summary>
    RandomSample,

    /// <summary>Every chart. The most accurate.</summary>
    AllCharts,
}

/// <summary>Chooses the charts a savings estimate is made from.</summary>
public static class SampleSelector
{
    /// <summary>How many charts a <see cref="SamplingMode.RandomSample"/> holds unless asked otherwise.</summary>
    public const int DefaultSampleSize = 10;

    /// <summary>
    /// The charts of <paramref name="library"/> to estimate from. A random sample is the <paramref name="sampleSize"/> charts
    /// whose relative paths hash lowest (SHA-256, not <see cref="string.GetHashCode()"/>, which changes between runs), so the
    /// same library gives the same sample however it's ordered, and adding a chart changes the sample by at most one.
    /// </summary>
    /// <param name="selected">The chart for <see cref="SamplingMode.SingleChart"/>.</param>
    /// <exception cref="ArgumentException"><see cref="SamplingMode.SingleChart"/> without a <paramref name="selected"/> chart that's in the library.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleSize"/> is below 1.</exception>
    public static IReadOnlyList<ChartAssets> Select(IReadOnlyList<ChartAssets> library, SamplingMode mode, SongEntry? selected = null, int sampleSize = DefaultSampleSize)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 1);

        switch (mode)
        {
            case SamplingMode.AllCharts:
                return library;

            case SamplingMode.SingleChart:
                if (selected is null)
                    throw new ArgumentException("A single chart estimate needs a chart.", nameof(selected));
                var chart = library.FirstOrDefault(c => OutputPaths.PathComparer.Equals(c.Entry.FullPath, selected.FullPath))
                    ?? throw new ArgumentException($"'{selected.FullPath}' isn't in the library.", nameof(selected));
                return [chart];

            case SamplingMode.RandomSample:
                return [.. library
                    .Select(c => (Chart: c, Path: NormalizedPath(c.Entry), Key: KeyOf(c.Entry)))
                    .OrderBy(c => c.Key).ThenBy(c => c.Path, StringComparer.Ordinal)
                    .Take(sampleSize)
                    .Select(c => c.Chart)];

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private static string NormalizedPath(SongEntry entry) => entry.RelativePath.Replace('\\', '/');

    private static ulong KeyOf(SongEntry entry) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedPath(entry))));
}

/// <summary>What one codec is estimated to save.</summary>
/// <param name="CodecKey">The input codec, as <c>video/vp9</c>, <c>audio/ogg</c>, <c>image/png</c>.</param>
/// <param name="AssetsSampled">How many of its files were estimated.</param>
/// <param name="AssetsTotal">How many of its files the library has.</param>
/// <param name="SampledInputBytes">The size of the files that were estimated.</param>
/// <param name="SampledOutputBytes">What they're estimated to come to. Where the optimizer says one's already optimal, that's its own size.</param>
/// <param name="TotalInputBytes">The size of all the library's files in this codec.</param>
public sealed record CodecSavings(string CodecKey, int AssetsSampled, int AssetsTotal, long SampledInputBytes, long SampledOutputBytes, long TotalInputBytes)
{
    /// <summary>Whether any file of the codec was estimated.</summary>
    public bool HasEstimate => AssetsSampled > 0 && SampledInputBytes > 0;

    /// <summary>The sampled output over the sampled input; 1 when nothing was estimated. Above 1, the codec gets larger.</summary>
    public double Ratio => HasEstimate ? (double)SampledOutputBytes / SampledInputBytes : 1;

    /// <summary>What all the library's files in the codec are expected to come to: the sample's ratio applied to their size.</summary>
    public long EstimatedOutputBytes => (long)Math.Round(TotalInputBytes * Ratio);

    /// <summary>What's expected to be saved across the library; negative when the codec would get larger.</summary>
    public long EstimatedSavingBytes => TotalInputBytes - EstimatedOutputBytes;
}

/// <summary>What a strategy is estimated to save across a library, by codec.</summary>
/// <param name="Strategy">The strategy.</param>
/// <param name="Mode">How the charts were chosen.</param>
/// <param name="ChartsSampled">How many charts were estimated.</param>
/// <param name="ChartsTotal">How many charts the library has.</param>
/// <param name="Codecs">Each codec, by key.</param>
public sealed record SavingsEstimate(OptimizeStrategy Strategy, SamplingMode Mode, int ChartsSampled, int ChartsTotal, IReadOnlyList<CodecSavings> Codecs)
{
    /// <summary>The library's size in the codecs that were estimated.</summary>
    public long TotalInputBytes => Codecs.Sum(c => c.TotalInputBytes);

    /// <summary>What's expected to be saved across all the codecs.</summary>
    public long EstimatedSavingBytes => Codecs.Sum(c => c.EstimatedSavingBytes);
}
