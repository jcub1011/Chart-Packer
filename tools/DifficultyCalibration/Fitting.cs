namespace DifficultyCalibration;

/// <summary>Fits the rank thresholds to rated parts (docs/difficulty-model.md §7.3).</summary>
internal static class Fitting
{
    private const int Top = 6;

    // The fewest charts a tier's median is taken from; a tier with fewer takes the gap of the one before.
    private const int MinCount = 5;

    /// <summary>Each tier's median score and how many charts it's from, for the output.</summary>
    public static string Medians(IReadOnlyList<Rated> rated) => string.Join("  ", Enumerable.Range(0, Top + 1).Select(t =>
    {
        var at = rated.Where(r => Math.Min(r.Part.Reported, Top) == t).Select(r => r.Score).Order().ToList();
        return at.Count == 0 ? $"{t}: -" : $"{t}: {Metrics.Quantile(at, 0.5):0.0}/{at.Count}";
    }));

    /// <summary>
    /// Thresholds halfway between neighbouring reported tiers' median scores (docs/difficulty-model.md §7.3): the typical
    /// chart of each tier lands in it, whatever the spread. Tiers past 6 count as 6; a tier with too few charts takes the
    /// gap of the one before.
    /// </summary>
    public static IReadOnlyList<double> Midpoints(IReadOnlyList<Rated> rated)
    {
        var medians = Enumerable.Range(0, Top + 1).Select(t =>
        {
            var at = rated.Where(r => Math.Min(r.Part.Reported, Top) == t).Select(r => r.Score).Order().ToList();
            return at.Count < MinCount ? double.NaN : Metrics.Quantile(at, 0.5);
        }).ToArray();
        var theta = new double[Top + 1];
        for (int k = 1; k <= Top; k++)
        {
            double gap = double.IsNaN(medians[k]) || double.IsNaN(medians[k - 1]) ? theta[k - 1] - theta[Math.Max(0, k - 2)] : (medians[k] + medians[k - 1]) / 2;
            theta[k] = Math.Max(theta[k - 1] + 0.1, double.IsNaN(medians[k]) || double.IsNaN(medians[k - 1]) ? theta[k - 1] + gap : gap);
        }

        return [.. theta.Select(t => Math.Round(t, 1))];
    }

    /// <summary>
    /// The thresholds θ₀ to θ₆ that put the fewest tiers between the ranks and the reported tiers (the mean absolute tier
    /// error), each in the gap between the scores of neighbouring tiers. Reported tiers past 6 count as 6.
    /// </summary>
    public static IReadOnlyList<double> Thresholds(IReadOnlyList<Rated> rated)
    {
        var points = rated.Select(r => (Score: r.Score, Tier: Math.Min(r.Part.Reported, Top))).OrderBy(p => p.Score).ToArray();
        double[] scores = [.. points.Select(p => p.Score)];

        // Starts halfway between neighbouring tiers' median scores.
        var medians = Enumerable.Range(0, Top + 1).Select(t =>
        {
            var at = points.Where(p => p.Tier == t).Select(p => p.Score).ToList();
            return at.Count == 0 ? double.NaN : Metrics.Quantile(at, 0.5);
        }).ToArray();
        var theta = new double[Top + 1];
        for (int k = 1; k <= Top; k++)
            theta[k] = Math.Max(theta[k - 1], double.IsNaN(medians[k]) || double.IsNaN(medians[k - 1]) ? theta[k - 1] + 10 : (medians[k] + medians[k - 1]) / 2);

        for (int round = 0; round < 50; round++)
        {
            bool moved = false;
            for (int k = 1; k <= Top; k++)
            {
                double low = theta[k - 1], high = k < Top ? theta[k + 1] : double.PositiveInfinity;
                int from = Array.FindIndex(scores, s => s >= low);
                int to = Array.FindIndex(scores, s => s >= high);
                if (from < 0)
                    continue;
                if (to < 0)
                    to = scores.Length;

                // Cutting before index c: those before are tier k - 1, the rest tier k.
                double cost = 0;
                for (int j = from; j < to; j++)
                    cost += Math.Abs(k - points[j].Tier);
                double best = cost;
                var bests = new List<int> { from };
                for (int c = from + 1; c <= to; c++)
                {
                    int t = points[c - 1].Tier;
                    cost += Math.Abs(k - 1 - t) - Math.Abs(k - t);
                    if (cost < best - 1e-9)
                    {
                        best = cost;
                        bests = [c];
                    }
                    else if (Math.Abs(cost - best) < 1e-9)
                        bests.Add(c);
                }

                int cut = bests[bests.Count / 2];
                double value = cut <= from ? (from == 0 ? scores[0] : (scores[from - 1] + scores[from]) / 2)
                    : cut >= scores.Length ? scores[^1] + 1
                    : (scores[cut - 1] + scores[cut]) / 2;
                value = Math.Clamp(value, low, double.IsInfinity(high) ? value : high);
                if (Math.Abs(value - theta[k]) > 1e-6)
                {
                    theta[k] = value;
                    moved = true;
                }
            }

            if (!moved)
                break;
        }

        return [.. theta.Select(t => Math.Round(t, 1))];
    }
}
