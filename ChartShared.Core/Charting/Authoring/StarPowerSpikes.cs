using ChartShared.Core.Charting.Difficulty;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>
/// The hard passages of a part (<see cref="StarPowerGenerator"/>), where star power is best activated going in: the
/// difficulty model's score of each stretch of <see cref="StarPowerParameters.SpikeWindowMeasures"/> measures, from
/// each measure line, rated on its own, smoothed, and its peaks that rise furthest above what's around them.
/// </summary>
/// <remarks>
/// <para>Rated on its own, a stretch has none of the endurance built up before it, so a later passage isn't ranked
/// harder for coming later. Stretches without a note are left out, so a passage after a rest rises from what came before
/// the rest.</para>
/// <para>A peak's prominence is how far it rises above the higher of the lowest points between it and a higher peak on
/// each side, as a mountain's is. With no higher peak after it, only the side before counts: a part that ends on its
/// hardest passage has it. One at the very start rises from nothing, so it has none, as there's no star power yet to
/// activate. A passage starts where it rises halfway from before it to its peak.</para>
/// </remarks>
internal static class StarPowerSpikes
{
    /// <summary>
    /// The hard passages, most prominent first, from <paramref name="notes"/>, the source as the difficulty model reads it,
    /// rated as <paramref name="difficulty"/> says.
    /// </summary>
    public static List<StarPowerSpike> Find(StarPowerContext c, IReadOnlyList<DifficultyNote> notes, DifficultyParameters difficulty)
    {
        var p = c.Parameters;
        var timeline = c.Timeline;
        var lines = timeline.MeasureLines;
        int window = Math.Max(1, p.SpikeWindowMeasures);
        var times = notes.Select(static n => n.TimeMs).ToArray();

        // Each stretch with notes, by its first measure, and its score.
        var measures = new List<int>();
        var scores = new List<double>();
        int last = timeline.MeasureIndexAt(c.LastChordTick);
        for (int m = timeline.MeasureIndexAt(c.Chords[0].Tick); m <= last && m + window < lines.Length; m++)
        {
            double from = timeline.SecondsAt(lines[m]) * 1000, to = timeline.SecondsAt(lines[m + window]) * 1000;
            int lo = LowerBound(times, from), hi = LowerBound(times, to);
            if (hi == lo)
                continue;
            var stretch = new DifficultyNote[hi - lo];
            for (int i = lo; i < hi; i++)
                stretch[i - lo] = notes[i];
            measures.Add(m);
            scores.Add(DifficultyAnalyzer.Analyze(stretch, new DifficultySpan(from, to), difficulty).ChartScore);
        }

        int n = scores.Count;
        if (n < 3)
            return [];
        var t = new double[n];
        for (int i = 0; i < n; i++)
            t[i] = (0.25 * scores[Math.Max(0, i - 1)]) + (0.5 * scores[i]) + (0.25 * scores[Math.Min(n - 1, i + 1)]);

        double range = t.Max() - t.Min();
        double median = t.Order().ElementAt(n / 2);
        double least = Math.Max(p.MinProminence, p.MinProminenceShare * range);
        var spikes = new List<StarPowerSpike>();
        for (int i = 0; i < n; i++)
        {
            // A peak, the first of a level run of them.
            if ((i > 0 && t[i] <= t[i - 1]) || (i + 1 < n && t[i + 1] > t[i]) || t[i] < median)
                continue;

            double left = t[i];
            for (int k = i - 1; k >= 0 && t[k] <= t[i]; k--)
                left = Math.Min(left, t[k]);
            double right = double.NegativeInfinity;
            bool higherAfter = false;
            double low = t[i];
            for (int k = i + 1; k < n; k++)
            {
                if (t[k] > t[i])
                {
                    higherAfter = true;
                    break;
                }

                low = Math.Min(low, t[k]);
            }

            if (higherAfter)
                right = low;
            double prominence = t[i] - Math.Max(left, right);
            if (prominence < least)
                continue;

            int start = i;
            while (start > 0 && t[start - 1] >= t[i] - (prominence / 2))
                start--;
            long startTick = lines[Math.Min(lines.Length - 1, measures[start] + (window / 2))];
            spikes.Add(new StarPowerSpike(startTick, lines[measures[i]], prominence, t[i]));
        }

        return Merge(spikes, timeline, p);
    }

    // Spikes nearer than the separation as one, the more prominent (the earlier on a tie), most prominent first.
    private static List<StarPowerSpike> Merge(List<StarPowerSpike> spikes, StarPowerTimeline timeline, StarPowerParameters p)
    {
        var kept = new List<StarPowerSpike>();
        foreach (var spike in spikes.OrderByDescending(static s => s.Prominence).ThenBy(static s => s.StartTick))
        {
            double at = timeline.MeasureAt(spike.StartTick);
            if (kept.All(k => Math.Abs(timeline.MeasureAt(k.StartTick) - at) >= p.MinSpikeSeparationMeasures))
                kept.Add(spike);
        }

        return kept;
    }

    private static int LowerBound(double[] values, double value)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (values[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}
