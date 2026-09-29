using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using ChartPacker.Audio.Decoding;

namespace DecodeBench;

/// <summary>A column worked out from the summary, blank where it doesn't apply.</summary>
internal sealed class DerivedColumn(string name, string legend, Func<Summary, BenchmarkCase, string?> value) : IColumn
{
    public string Id => nameof(DerivedColumn) + "." + ColumnName;

    public string ColumnName { get; } = name;

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend { get; } = legend;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => value(summary, benchmarkCase) ?? "-";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public bool IsAvailable(Summary summary) => true;

    /// <summary>The columns DecodeBench adds: the stem's length, the real-time factor, and how decoding compares.</summary>
    public static IColumn[] All(IReadOnlyList<StemLocation> stems)
    {
        var seconds = stems.ToDictionary(s => s.Label, s => s.Seconds);

        return
        [
            new DerivedColumn("Length", "How long the stem is, in seconds", (_, c) => $"{seconds[Stem(c)]:0.0} s"),
            new DerivedColumn("RTF", "Real-time factor: seconds of audio read per second, from the median", (summary, c) =>
                c.Descriptor.WorkloadMethod.Name == nameof(DecodeBenchmarks.Open) || Median(summary, c) is not { } median
                    ? null
                    : $"{seconds[Stem(c)] / median:0}×"),
            new DerivedColumn("Decode share", "How much of the one-thread peaks read is the decode, from the medians", (summary, c) =>
                c.Descriptor.WorkloadMethod.Name == nameof(DecodeBenchmarks.PeaksOneThread)
                    ? Ratio(summary, c, nameof(DecodeBenchmarks.Decode), Backend(c), "0%")
                    : null),
            new DerivedColumn("FFmpeg speed-up", "The same benchmark's median on the managed decoders over this one on FFmpeg's", (summary, c) =>
                Backend(c) == AudioBackend.Ffmpeg
                    ? Ratio(summary, c, c.Descriptor.WorkloadMethod.Name, AudioBackend.Managed, "0.0×")
                    : null),
        ];
    }

    private static string Stem(BenchmarkCase benchmarkCase) => (string)benchmarkCase.Parameters[nameof(DecodeBenchmarks.Audio)]!;

    private static AudioBackend Backend(BenchmarkCase benchmarkCase) => (AudioBackend)benchmarkCase.Parameters[nameof(DecodeBenchmarks.Backend)]!;

    /// <summary>The median in seconds, or null when it didn't run.</summary>
    private static double? Median(Summary summary, BenchmarkCase benchmarkCase) =>
        summary[benchmarkCase]?.ResultStatistics?.Median is { } nanoseconds ? nanoseconds / 1e9 : null;

    /// <summary>The median of <paramref name="method"/> on <paramref name="backend"/> for the same stem and job, over this case's.</summary>
    private static string? Ratio(Summary summary, BenchmarkCase benchmarkCase, string method, AudioBackend backend, string format)
    {
        var other = summary.BenchmarksCases.FirstOrDefault(c =>
            c.Descriptor.WorkloadMethod.Name == method
            && Backend(c) == backend
            && Stem(c) == Stem(benchmarkCase)
            && c.Job.DisplayInfo == benchmarkCase.Job.DisplayInfo);
        if (other is null || Median(summary, other) is not { } theirs || Median(summary, benchmarkCase) is not { } ours)
            return null;

        return (theirs / ours).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }
}
