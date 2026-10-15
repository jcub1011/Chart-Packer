namespace ChartPacker.Core.Charting.Analysis;

/// <summary>Arithmetic on beat and onset times that both the analysis and the editor use.</summary>
public static class BeatMath
{
    /// <summary>
    /// The least-squares line <c>time = intercept + i × period</c> through <paramref name="beats"/>, where <c>i</c> is each
    /// beat's index, and the root mean square of how far the beats are from it.
    /// </summary>
    /// <remarks>
    /// A tempo read from the line's slope is far more precise than any one interval: a constant lateness in every beat
    /// doesn't change it at all, and jitter averages out over the beats.
    /// </remarks>
    /// <exception cref="ArgumentException">There are fewer than two beats.</exception>
    public static (double Intercept, double Period, double RmsResidual) FitLine(ReadOnlySpan<double> beats)
    {
        if (beats.Length < 2)
            throw new ArgumentException("A line needs two beats at least.", nameof(beats));

        int n = beats.Length;
        double meanI = (n - 1) / 2.0;
        double meanT = 0;
        foreach (double t in beats)
            meanT += t;
        meanT /= n;

        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            double di = i - meanI;
            sxy += di * (beats[i] - meanT);
            sxx += di * di;
        }

        double period = sxy / sxx;
        double intercept = meanT - (period * meanI);
        double squares = 0;
        for (int i = 0; i < n; i++)
        {
            double residual = beats[i] - (intercept + (period * i));
            squares += residual * residual;
        }

        return (intercept, period, Math.Sqrt(squares / n));
    }

    /// <summary>The tempo of <paramref name="beats"/>, in BPM, from the slope of <see cref="FitLine"/>.</summary>
    /// <exception cref="ArgumentException">There are fewer than two beats.</exception>
    public static double BpmOf(ReadOnlySpan<double> beats) => 60 / FitLine(beats).Period;

    /// <summary>
    /// <paramref name="proposal"/> at twice the tempo: a beat halfway between each two, as sure as the less sure of them.
    /// </summary>
    public static BeatProposal Doubled(BeatProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Beats.Count < 2)
            return proposal;

        int count = proposal.Beats.Count;
        var beats = new double[(count * 2) - 1];
        var confidence = new float[beats.Length];
        for (int i = 0; i < count; i++)
        {
            beats[i * 2] = proposal.Beats[i];
            confidence[i * 2] = proposal.BeatConfidence[i];
            if (i + 1 < count)
            {
                beats[(i * 2) + 1] = (proposal.Beats[i] + proposal.Beats[i + 1]) / 2;
                confidence[(i * 2) + 1] = Math.Min(proposal.BeatConfidence[i], proposal.BeatConfidence[i + 1]);
            }
        }

        var regions = proposal.Regions
            .Select(r => r with { FirstBeat = r.FirstBeat * 2, LastBeat = r.LastBeat * 2, Bpm = r.Bpm * 2 })
            .ToList();
        return new BeatProposal(beats, confidence, regions, proposal.Bpm * 2, false);
    }

    /// <summary>
    /// <paramref name="proposal"/> at half the tempo: every other beat, starting with beat <paramref name="phase"/> (0 or 1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> isn't 0 or 1.</exception>
    public static BeatProposal Halved(BeatProposal proposal, int phase = 0)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentOutOfRangeException.ThrowIfNegative(phase);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(phase, 1);

        var beats = new List<double>();
        var confidence = new List<float>();
        for (int i = phase; i < proposal.Beats.Count; i += 2)
        {
            beats.Add(proposal.Beats[i]);
            confidence.Add(proposal.BeatConfidence[i]);
        }

        var regions = new List<TempoRegion>();
        foreach (var region in proposal.Regions)
        {
            // The kept beats are those at phase, phase + 2, …: kept beat k is beat phase + 2k.
            int first = (int)Math.Ceiling((region.FirstBeat - phase) / 2.0);
            int last = (int)Math.Floor((region.LastBeat - phase) / 2.0);
            first = Math.Max(first, 0);
            last = Math.Min(last, beats.Count - 1);
            if (last > first)
                regions.Add(region with { FirstBeat = first, LastBeat = last, Bpm = region.Bpm / 2 });
        }

        return new BeatProposal(beats, confidence, regions, proposal.Bpm / 2, false);
    }

    /// <summary>
    /// The index of the time in <paramref name="sorted"/> nearest <paramref name="seconds"/>, or -1 when none is within
    /// <paramref name="maxDistance"/>. Of two as near, the earlier.
    /// </summary>
    public static int NearestIndex(IReadOnlyList<double> sorted, double seconds, double maxDistance)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
            return -1;

        // The first time at or after seconds.
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < seconds)
                lo = mid + 1;
            else
                hi = mid;
        }

        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = Math.Max(0, lo - 1); i <= Math.Min(sorted.Count - 1, lo); i++)
        {
            double distance = Math.Abs(sorted[i] - seconds);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return bestDistance <= maxDistance ? best : -1;
    }
}
