using ChartShared.Core.Charting;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// One beat of the source part as the network reads it (spec §3.3): the source pattern plus
/// per-beat features. Conditioning shared by a run (target, source, instrument, style, tier)
/// travels separately in <see cref="RunConditioning"/>.
/// </summary>
public sealed record BeatFrame(
    long BeatStartTick,
    int MeasureIndex,
    int BeatInMeasure,
    int SourcePatternId,
    int[] SourceOffsets48,
    long[] SourceTicks,
    int GapBucket,
    bool PhraseStart,
    bool SectionStart,
    bool HasSustain,
    bool StarPower,
    bool Solo,
    string RiffKey,
    int RiffRepeat);

/// <summary>Run-level conditioning, shared by every beat (§3.3).</summary>
public sealed record RunConditioning(
    ChartDifficulty Target,
    ChartDifficulty Source,
    ChartInstrumentKind Kind,
    int StyleId,
    int TierBucket = 7);

/// <summary>
/// Splits the source part into beats by the chart's time signatures and encodes each beat's
/// onsets as a pattern (§3.4 steps 1–2). Beats the source doesn't play in are passed over for
/// scoring but kept as context (empty previous beats are information).
/// </summary>
public static class BeatPipeline
{
    /// <summary>Beat schema version the packs are trained on; must match the app's (§5.4).</summary>
    public const int BeatSchemaVersion = 1;

    /// <summary>
    /// Extracts every beat from the source's first onset to its last, in order. When
    /// <paramref name="range"/> is given, only beats overlapping it are returned (plus one
    /// context beat before, when there is one).
    /// </summary>
    public static IReadOnlyList<BeatFrame> Extract(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty source,
        (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var whole = chart.PartOf(part);
        var track = whole?[source];
        if (track is null || track.Notes.Count == 0)
            return [];

        var map = chart.TempoMap.ToTempoMap();
        var signatures = map.TimeSignatures;
        if (signatures.Count == 0)
            return [];

        long[] ticks = [.. track.Notes.Select(static n => n.Tick).Distinct().Order()];
        var byTick = track.Notes.GroupBy(static n => n.Tick).ToDictionary(static g => g.Key, static g => g.ToArray());

        long lo = range?.From ?? ticks[0];
        long hi = range?.To ?? ticks[^1];

        var onsetsInScope = ticks.Where(t => t >= lo - map.Resolution * 4 && t <= hi).ToList();
        if (onsetsInScope.Count == 0)
            return [];

        long firstBeat = BeatStartOf(signatures, map.Resolution, onsetsInScope[0]);
        long lastBeat = BeatStartOf(signatures, map.Resolution, hi);
        if (range is not null)
            firstBeat = BeatStartOf(signatures, map.Resolution, Math.Max(0, lo - map.Resolution));

        var sections = new HashSet<long>(SectionTicks(chart));
        var phrases = whole!.Phrases.AsSpan().ToArray();

        var frames = new List<BeatFrame>();
        long prevOnset = -1;
        var riffCounts = new Dictionary<string, int>();
        var riffKeysInOrder = new List<string>();

        for (long beat = firstBeat; beat <= lastBeat; beat += BeatLengthAt(signatures, map.Resolution, beat))
        {
            long beatEnd = beat + BeatLengthAt(signatures, map.Resolution, beat);
            var onsetTicks = onsetsInScope.Where(t => t >= beat && t < beatEnd).Order().ToList();
            var offsets = new List<int>();
            var srcTicks = new List<long>();
            foreach (long t in onsetTicks)
            {
                srcTicks.Add(t);
                offsets.Add((int)((t - beat) * BeatPattern.UnitsPerBeat / BeatLengthAt(signatures, map.Resolution, beat)));
            }

            // Measure position.
            var (measureIndex, beatInMeasure) = MeasurePosition(signatures, map.Resolution, beat);

            int patternId = offsets.Count == 0 ? BeatPattern.EmptyId : BeatPattern.EncodeSource(offsets);

            int gapBucket = prevOnset < 0 ? 0 : GapBucketOf(onsetTicks.Count > 0 ? onsetTicks[0] - prevOnset : beatEnd - prevOnset, chart.Resolution);
            bool phraseStart = onsetTicks.Count > 0 && (prevOnset < 0 || onsetTicks[0] - prevOnset >= chart.Resolution);
            bool sectionStart = sections.Any(s => s >= beat && s < beatEnd);
            bool sustain = onsetTicks.Any(t => byTick[t].Any(n => n.Length >= chart.Resolution));
            bool sp = phrases.Any(p => p.Kind == PhraseKind.StarPower && onsetTicks.Any(p.Covers));
            bool solo = phrases.Any(p => p.Kind == PhraseKind.Solo && onsetTicks.Any(p.Covers));

            string riffKey = RiffKeyOf(signatures, map.Resolution, beat, onsetsInScope);
            if (!riffCounts.TryGetValue(riffKey, out int repeat))
                repeat = 0;
            riffCounts[riffKey] = repeat + 1;
            riffKeysInOrder.Add(riffKey);

            frames.Add(new BeatFrame(beat, measureIndex, beatInMeasure, patternId,
                [.. offsets], [.. srcTicks], gapBucket, phraseStart, sectionStart,
                sustain, sp, solo, riffKey, repeat));

            if (onsetTicks.Count > 0)
                prevOnset = onsetTicks[^1];
        }

        return frames;
    }

    /// <summary>Buckets a tick gap into 8 log-ish bands relative to the resolution.</summary>
    public static int GapBucketOf(long gapTicks, int resolution)
    {
        if (gapTicks <= 0)
            return 0;
        double beats = (double)gapTicks / Math.Max(1, resolution);
        if (beats < 0.26)
            return 1;
        if (beats < 0.51)
            return 2;
        if (beats < 1.01)
            return 3;
        if (beats < 2.01)
            return 4;
        if (beats < 4.01)
            return 5;
        if (beats < 8.01)
            return 6;
        return 7;
    }

    /// <summary>Tier 0–6 to buckets; parts with no tier are their own bucket (§3.3).</summary>
    public static int TierBucketOf(int? tier) => tier is not { } t ? 7 : Math.Clamp(t, 0, 6);

    private static long BeatStartOf(IReadOnlyList<TimeSignature> signatures, int resolution, long tick)
    {
        var sig = SignatureAt(signatures, tick);
        long beat = BeatLengthOf(sig, resolution);
        long offset = Math.Max(0, tick - sig.Tick);
        return sig.Tick + (offset / beat * beat);
    }

    private static long BeatLengthAt(IReadOnlyList<TimeSignature> signatures, int resolution, long tick) =>
        BeatLengthOf(SignatureAt(signatures, tick), resolution);

    private static long BeatLengthOf(TimeSignature sig, int resolution) =>
        Math.Max(1, resolution * 4L / Math.Max(1, sig.Denominator));

    private static TimeSignature SignatureAt(IReadOnlyList<TimeSignature> signatures, long tick)
    {
        var current = signatures[0];
        foreach (var s in signatures)
        {
            if (s.Tick > tick)
                break;
            current = s;
        }

        return current;
    }

    private static (int Measure, int BeatInMeasure) MeasurePosition(
        IReadOnlyList<TimeSignature> signatures, int resolution, long beat)
    {
        var sig = SignatureAt(signatures, beat);
        long beatLen = BeatLengthOf(sig, resolution);
        long measureLen = beatLen * Math.Max(1, sig.Numerator);
        long offset = Math.Max(0, beat - sig.Tick);
        return ((int)(offset / measureLen), (int)((offset % measureLen) / beatLen));
    }

    private static string RiffKeyOf(
        IReadOnlyList<TimeSignature> signatures, int resolution, long beat, List<long> onsets)
    {
        var sig = SignatureAt(signatures, beat);
        long beatLen = BeatLengthOf(sig, resolution);
        long measureLen = beatLen * Math.Max(1, sig.Numerator);
        long from = sig.Tick + (Math.Max(0, beat - sig.Tick) / measureLen * measureLen);
        var inMeasure = onsets.Where(t => t >= from && t < from + measureLen)
            .Select(t => (t - from) * BeatPattern.UnitsPerBeat / beatLen).Order().ToList();
        return $"{measureLen}:{string.Join(",", inMeasure)}";
    }

    private static IReadOnlyList<long> SectionTicks(EditableChart chart)
    {
        var sections = new List<long>();
        foreach (var e in chart.Events.AsSpan().ToArray())
        {
            if (e.Text.StartsWith("[section", StringComparison.OrdinalIgnoreCase))
                sections.Add(e.Tick);
        }

        return sections;
    }
}
