namespace ChartPacker.Audio.Analysis;

/// <summary>
/// Places beats on the onset flux by dynamic programming (after Ellis, 2007): each frame's best score is its own flux
/// plus the best of the frames a beat period before it, less a cost for how far that gap is from the local tempo.
/// </summary>
/// <remarks>
/// The local tempo is every tempo the <see cref="Tempogram"/>'s path takes within a few seconds, so where the tempo
/// steps, gaps of either tempo cost nothing and the beats follow the step rather than averaging across it.
/// </remarks>
internal static class BeatTracker
{
    /// <summary>How sharply a gap's cost grows with its distance from the tempo, in log terms.</summary>
    private const double Tightness = 100;

    /// <summary>How far round each moment the tempogram's path gives the local tempos.</summary>
    private const double HypothesisSeconds = 3;

    /// <summary>The beats' frames, from <paramref name="from"/> up to <paramref name="to"/>, ascending.</summary>
    public static List<int> Track(ReadOnlySpan<float> flux, int from, int to, double framesPerSecond, Tempogram tempogram, CancellationToken cancellationToken)
    {
        int length = to - from;
        if (length < 4)
            return [];

        var local = Smoothed(flux, from, to);
        double mean = 0, squares = 0;
        foreach (float v in local)
        {
            mean += v;
            squares += v * (double)v;
        }

        mean /= length;
        double deviation = Math.Sqrt(Math.Max(0, (squares / length) - (mean * mean)));
        if (deviation < 1e-9)
            return [];
        for (int i = 0; i < length; i++)
            local[i] = (float)(local[i] / deviation);

        var score = new double[length];
        var previous = new int[length];
        double[] penalty = [];
        int minGap = 1, maxGap = 0;
        int window = -1;

        for (int i = 0; i < length; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int w = tempogram.WindowAt(from + i);
            if (w != window)
            {
                window = w;
                (penalty, minGap, maxGap) = PenaltiesNear(tempogram, w, framesPerSecond);
            }

            double best = double.NegativeInfinity;
            int bestFrom = -1;
            for (int gap = minGap; gap <= Math.Min(maxGap, i); gap++)
            {
                double candidate = score[i - gap] - penalty[gap];
                if (candidate > best)
                {
                    best = candidate;
                    bestFrom = i - gap;
                }
            }

            score[i] = local[i] + (bestFrom >= 0 ? best : 0);
            previous[i] = bestFrom;
        }

        // The last beat: the best score within the longest gap of the end.
        int end = length - 1;
        for (int i = Math.Max(0, length - 1 - maxGap); i < length; i++)
        {
            if (score[i] > score[end])
                end = i;
        }

        var beats = new List<int>();
        for (int i = end; i >= 0; i = previous[i])
            beats.Add(from + i);
        beats.Reverse();
        return beats;
    }

    /// <summary>The cost of each gap near window <paramref name="window"/>, for the local tempos there.</summary>
    private static (double[] Penalty, int MinGap, int MaxGap) PenaltiesNear(Tempogram tempogram, int window, double framesPerSecond)
    {
        int reach = (int)Math.Ceiling(HypothesisSeconds * framesPerSecond / tempogram.FramesPerHop);
        var periods = new List<double>();
        int at = tempogram.Path[window];

        // The tempo here first, then the others nearby, nearest first; tempos within 3% of one kept are left out.
        foreach (int w in Enumerable.Range(Math.Max(0, window - reach), Math.Min(tempogram.WindowCount, window + reach + 1) - Math.Max(0, window - reach))
            .OrderBy(w => Math.Abs(w - window)))
        {
            double bpm = Tempogram.BpmOf(w == window ? at : tempogram.Path[w]);
            double period = 60 * framesPerSecond / bpm;
            if (periods.All(p => Math.Abs(Math.Log(p / period)) > Math.Log(1.03)))
                periods.Add(period);
            if (periods.Count == 4)
                break;
        }

        int minGap = Math.Max(1, (int)Math.Floor(periods.Min() * 0.5));
        int maxGap = (int)Math.Ceiling(periods.Max() * 2);
        var penalty = new double[maxGap + 1];
        for (int gap = minGap; gap <= maxGap; gap++)
        {
            double least = double.PositiveInfinity;
            foreach (double period in periods)
            {
                double log = Math.Log(gap / period);
                least = Math.Min(least, Tightness * log * log);
            }

            penalty[gap] = least;
        }

        return (penalty, minGap, maxGap);
    }

    /// <summary>The flux smoothed by a Gaussian of two frames.</summary>
    private static float[] Smoothed(ReadOnlySpan<float> flux, int from, int to)
    {
        const int radius = 6;
        const double sigma = 2;
        Span<double> kernel = stackalloc double[(radius * 2) + 1];
        double total = 0;
        for (int k = -radius; k <= radius; k++)
            total += kernel[k + radius] = Math.Exp(-0.5 * (k / sigma) * (k / sigma));

        var smoothed = new float[to - from];
        for (int i = 0; i < smoothed.Length; i++)
        {
            double sum = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int m = from + i + k;
                if (m >= from && m < to)
                    sum += flux[m] * kernel[k + radius];
            }

            smoothed[i] = (float)(sum / total);
        }

        return smoothed;
    }
}
