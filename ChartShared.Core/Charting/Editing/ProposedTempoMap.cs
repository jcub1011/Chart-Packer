using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>How a proposed tempo map is written when it's accepted (charter-spec C14 item 4).</summary>
/// <param name="AnchorEveryMeasures">An anchor every this many measures, from measure 1; 0 for none.</param>
/// <param name="MergeToleranceMs">
/// Beats a tempo segment can run through without a marker of their own, as long as each is within this of where the
/// segment's tempo puts it; 0 for a marker on every beat.
/// </param>
public sealed record ProposalOptions(int AnchorEveryMeasures = 4, double MergeToleranceMs = 5)
{
    public static ProposalOptions Default { get; } = new();
}

/// <summary>
/// Turns proposed beat times into a tempo map (charter-spec C14 item 4), as one <see cref="ChangeTempoMap"/>: a tempo
/// marker where the tempo changes, and anchors every few measures and around each stretch too unsure to trust.
/// </summary>
/// <remarks>
/// <para><b>The whole song.</b> The proposal's downbeat goes on measure 2's line, and measure 1 is a lead-in: its tempo
/// is whatever makes it last until the beat before the first one placed (the lead-in convention of C14 item 5). A downbeat
/// on the chart's start goes on tick 0 instead, and one before it is skipped for the next. Beats before tick 0 are left
/// out. Every tempo marker and anchor the chart had is replaced; the time signatures stay.</para>
/// <para><b>A selection.</b> The proposal's first beat goes on the beat line nearest it, and the markers and anchors
/// between its first beat and its last are replaced. The segment before the first beat takes up the change, so it lands on
/// the proposal; after the last, the tempo that was in force there carries on, and the first anchor after it stays where
/// it is in the audio, as any tempo edit keeps it (C4 item 2).</para>
/// <para>A stretch too unsure to trust is one segment from the beat before it to the beat after, both anchored, for the
/// author to map by hand.</para>
/// </remarks>
public static class ProposedTempoMap
{
    /// <summary>The tempo map <paramref name="beats"/> make.</summary>
    /// <param name="beats">Audio seconds, ascending.</param>
    /// <param name="unsure">The first and last beat of each stretch too unsure to trust.</param>
    /// <param name="downbeat">The beat measure lines are counted from; it starts a measure.</param>
    /// <param name="delay">The song's delay: audio time is chart time plus this.</param>
    /// <param name="span">The ticks of the selection the beats were proposed for, or <see langword="null"/> for the whole song.</param>
    public static TempoEdit Build(EditableChart chart, IReadOnlyList<double> beats, IReadOnlyList<(int First, int Last)> unsure, int downbeat, ProposalOptions options, TimeSpan delay, bool keepTimes, (long From, long To)? span = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(unsure);
        ArgumentNullException.ThrowIfNull(options);
        if (beats.Count < 2)
            return TempoEdit.Refused("There are too few beats to make a tempo map of.");

        double delaySeconds = delay.TotalSeconds;
        var chartSeconds = beats.Select(b => b - delaySeconds).ToArray();
        var signatures = chart.TempoMap.TimeSignatures.Select(static s => s.ToTimeSignature()).ToList();
        return span is { } selection
            ? ForSpan(chart, chartSeconds, unsure, signatures, options, delay, keepTimes, selection)
            : ForSong(chart, chartSeconds, unsure, Math.Clamp(downbeat, 0, beats.Count - 1), signatures, options, delay, keepTimes);
    }

    private static TempoEdit ForSong(EditableChart chart, double[] seconds, IReadOnlyList<(int First, int Last)> unsure, int downbeat, List<TimeSignature> signatures, ProposalOptions options, TimeSpan delay, bool keepTimes)
    {
        int resolution = chart.Resolution;
        const double OnStart = 0.001;

        // A downbeat before the chart starts gives way to the next one after it.
        int perMeasure = SignatureAt(signatures, 0).Numerator;
        while (downbeat < seconds.Length && seconds[downbeat] < -OnStart)
            downbeat += perMeasure;
        if (downbeat >= seconds.Length)
            return TempoEdit.Refused("Every proposed downbeat is before the chart starts: lower the song's delay first.");

        long reference = Math.Abs(seconds[downbeat]) <= OnStart ? 0 : MeasureLength(signatures, 0, resolution);
        var ticks = TicksOf(seconds.Length, downbeat, reference, signatures, resolution);

        // From the start, then every beat after it.
        var points = new List<Point> { new(0, 0, IsBreak: false) };
        var unsureEdges = UnsureEdges(unsure, seconds.Length);
        for (int b = 0; b < seconds.Length; b++)
        {
            if (ticks[b] <= 0 || seconds[b] <= OnStart / 2 || unsureEdges.Inside.Contains(b))
                continue;
            if (ticks[b] <= points[^1].Tick || seconds[b] <= points[^1].Seconds)
                continue;
            points.Add(new Point(ticks[b], seconds[b], unsureEdges.Edges.Contains(b)));
        }

        if (points.Count < 2)
            return TempoEdit.Refused("No proposed beat is after the chart's start.");

        if (Segments(points, options, resolution) is not { } tempos)
            return TempoEdit.Refused("The proposed beats would need a tempo too fast or too slow for MIDI to hold.");

        var map = new TempoMap(resolution, tempos, signatures);
        var anchorTicks = MeasureAnchors(signatures, resolution, options.AnchorEveryMeasures, 0, points[^1].Tick)
            .Concat(points.Where(static p => p.IsBreak).Select(static p => p.Tick))
            .Distinct()
            .Order()
            .ToList();
        AddMarkers(tempos, anchorTicks, map);
        var anchors = anchorTicks.Select(t => new TempoAnchor(t, TempoMapEdits.Microseconds(map.SecondsAt(t) + delay.TotalSeconds))).ToList();
        return new TempoEdit(new ChangeTempoMap(tempos, chart.TempoMap.TimeSignatures, anchors, keepTimes));
    }

    private static TempoEdit ForSpan(EditableChart chart, double[] seconds, IReadOnlyList<(int First, int Last)> unsure, List<TimeSignature> signatures, ProposalOptions options, TimeSpan delay, bool keepTimes, (long From, long To) span)
    {
        int resolution = chart.Resolution;
        var old = chart.TempoMap.ToTempoMap();

        // The first beat on the beat line nearest it, as the chart is timed now.
        double firstTick = old.TickAt(seconds[0]);
        long first = old.BeatLines(Math.Max(0, (long)firstTick - (resolution * 4)), (long)firstTick + (resolution * 4))
            .Select(static l => l.Tick)
            .MinBy(t => Math.Abs(t - firstTick));
        if (first <= 0)
            return TempoEdit.Refused("The selection starts with the song: propose a tempo map for the whole song instead.");

        var ticks = TicksOf(seconds.Length, 0, first, signatures, resolution);
        long last = ticks[^1];
        var unsureEdges = UnsureEdges(unsure, seconds.Length);
        var points = new List<Point>();
        for (int b = 0; b < seconds.Length; b++)
        {
            if (unsureEdges.Inside.Contains(b) || (points.Count > 0 && (ticks[b] <= points[^1].Tick || seconds[b] <= points[^1].Seconds)))
                continue;
            points.Add(new Point(ticks[b], seconds[b], b == 0 || unsureEdges.Edges.Contains(b)));
        }

        if (points.Count < 2 || Segments(points, options, resolution) is not { } proposed)
            return TempoEdit.Refused("The proposed beats would need a tempo too fast or too slow for MIDI to hold.");

        // The segment before the first beat lands it on the proposal.
        var oldTempos = chart.TempoMap.Tempos.AsSpan().ToArray();
        var before = oldTempos.Where(t => t.Tick < first).ToList();
        if (before.Count == 0)
            return TempoEdit.Refused("The selection starts with the song: propose a tempo map for the whole song instead.");
        var leadIn = before[^1];
        long leadInTempo = TempoMapEdits.MicrosecondsFor(seconds[0] - old.SecondsAt(leadIn.Tick), first - leadIn.Tick, resolution);
        if (leadInTempo is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
            return TempoEdit.Refused("The tempo before the selection would have to be too fast or too slow to land on the proposal.");
        before[^1] = leadIn with { MicrosecondsPerQuarter = leadInTempo };

        // After the last beat, the tempo in force there before carries on.
        var tempos = before.Concat(proposed).ToList();
        if (tempos.All(t => t.Tick != last))
            tempos.Add(new TempoChange(last, TempoMapEdits.TempoAt(old, last)));
        else
            tempos[tempos.FindLastIndex(t => t.Tick == last)] = new TempoChange(last, TempoMapEdits.TempoAt(old, last));
        tempos.AddRange(oldTempos.Where(t => t.Tick > last));

        var map = new TempoMap(resolution, tempos, signatures);
        var anchorTicks = MeasureAnchors(signatures, resolution, options.AnchorEveryMeasures, first, last)
            .Concat(points.Where(static p => p.IsBreak).Select(static p => p.Tick))
            .Append(last)
            .Where(t => t >= first && t <= last)
            .Distinct()
            .Order()
            .ToList();
        AddMarkers(tempos, anchorTicks, map);
        var anchors = chart.TempoMap.Anchors.Where(a => a.Tick < first || a.Tick > last)
            .Concat(anchorTicks.Select(t => new TempoAnchor(t, TempoMapEdits.Microseconds(map.SecondsAt(t) + delay.TotalSeconds))))
            .OrderBy(static a => a.Tick)
            .ToList();

        // The first anchor after the proposal stays where it is in the audio.
        return TempoMapEdits.Fitted(chart, tempos, anchors, last, absorbFrom: last, delay, keepTimes, BlockedAnchor.Refuse);
    }

    /// <summary>
    /// Tempo markers through <paramref name="points"/>: each segment runs as far as every point in it stays within the
    /// tolerance of where its tempo puts it, and ends at a break. Null when a tempo is out of MIDI's range.
    /// </summary>
    private static List<TempoChange>? Segments(List<Point> points, ProposalOptions options, int resolution)
    {
        double tolerance = Math.Max(0, options.MergeToleranceMs) / 1000;
        var tempos = new List<TempoChange>();
        int start = 0;
        while (start < points.Count - 1)
        {
            int end = start + 1;
            while (end + 1 < points.Count && !points[end].IsBreak && Fits(points, start, end + 1, tolerance))
                end++;

            long tempo = TempoMapEdits.MicrosecondsFor(points[end].Seconds - points[start].Seconds, points[end].Tick - points[start].Tick, resolution);
            if (tempo is < 1 or > EditableTempoMap.MaxMicrosecondsPerQuarter)
                return null;
            tempos.Add(new TempoChange(points[start].Tick, tempo));
            start = end;
        }

        return tempos;
    }

    /// <summary>Whether every point between <paramref name="start"/> and <paramref name="end"/> is within <paramref name="tolerance"/> of the line between them.</summary>
    private static bool Fits(List<Point> points, int start, int end, double tolerance)
    {
        var a = points[start];
        var b = points[end];
        double perTick = (b.Seconds - a.Seconds) / (b.Tick - a.Tick);
        for (int i = start + 1; i < end; i++)
        {
            double expected = a.Seconds + ((points[i].Tick - a.Tick) * perTick);
            if (Math.Abs(expected - points[i].Seconds) > tolerance)
                return false;
        }

        return true;
    }

    /// <summary>Each beat's tick: <paramref name="reference"/>'s at <paramref name="referenceTick"/>, and the others a beat apart from it.</summary>
    private static long[] TicksOf(int count, int reference, long referenceTick, List<TimeSignature> signatures, int resolution)
    {
        var ticks = new long[count];
        ticks[reference] = referenceTick;
        for (int b = reference + 1; b < count; b++)
            ticks[b] = ticks[b - 1] + BeatLength(signatures, ticks[b - 1], resolution);
        for (int b = reference - 1; b >= 0; b--)
            ticks[b] = ticks[b + 1] - BeatLength(signatures, Math.Max(0, ticks[b + 1] - 1), resolution);
        return ticks;
    }

    /// <summary>The first ticks of measures 1 + n, 1 + 2n, … from <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private static IEnumerable<long> MeasureAnchors(List<TimeSignature> signatures, int resolution, int every, long from, long to)
    {
        if (every <= 0)
            yield break;
        long tick = 0;
        for (int measure = 1; tick <= to; measure++)
        {
            if (measure > 1 && (measure - 1) % every == 0 && tick >= from)
                yield return tick;
            tick += MeasureLength(signatures, tick, resolution);
        }
    }

    /// <summary>Adds a marker with the tempo in force at each of <paramref name="ticks"/> that has none, so it can be anchored.</summary>
    private static void AddMarkers(List<TempoChange> tempos, List<long> ticks, TempoMap map)
    {
        foreach (long tick in ticks)
        {
            if (tempos.All(t => t.Tick != tick))
                tempos.Add(new TempoChange(tick, TempoMapEdits.TempoAt(map, tick)));
        }

        tempos.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
    }

    /// <summary>The beats inside the unsure stretches, which are left out, and the beats either side, which break the segments there.</summary>
    private static (HashSet<int> Inside, HashSet<int> Edges) UnsureEdges(IReadOnlyList<(int First, int Last)> unsure, int count)
    {
        var inside = new HashSet<int>();
        var edges = new HashSet<int>();
        foreach (var (first, last) in unsure)
        {
            int from = Math.Clamp(first, 0, count - 1), to = Math.Clamp(last, 0, count - 1);
            if (to <= from)
                continue;
            edges.Add(from);
            edges.Add(to);
            for (int b = from + 1; b < to; b++)
                inside.Add(b);
        }

        return (inside, edges);
    }

    private static TimeSignature SignatureAt(List<TimeSignature> signatures, long tick)
    {
        var found = new TimeSignature(0, 4, 4);
        foreach (var signature in signatures)
        {
            if (signature.Tick > tick)
                break;
            found = signature;
        }

        return found;
    }

    private static long BeatLength(List<TimeSignature> signatures, long tick, int resolution) =>
        Math.Max(1, resolution * 4L / SignatureAt(signatures, tick).Denominator);

    private static long MeasureLength(List<TimeSignature> signatures, long tick, int resolution)
    {
        var signature = SignatureAt(signatures, tick);
        return Math.Max(1, signature.Numerator * resolution * 4L / signature.Denominator);
    }

    /// <summary>A beat as a tempo map must place it: its tick, its chart time, and whether a segment must end there.</summary>
    private readonly record struct Point(long Tick, double Seconds, bool IsBreak);
}
