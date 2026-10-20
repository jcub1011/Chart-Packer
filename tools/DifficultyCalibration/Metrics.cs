using ChartShared.Core.Charting.Difficulty;

namespace DifficultyCalibration;

/// <summary>What one set of parameters made of a part.</summary>
internal sealed record Rated(Part Part, double Score, double Rank, DifficultyAttributes Attributes, DifficultyAttribute Dominant);

/// <summary>How well ranks agree with the reported tiers.</summary>
internal static class Metrics
{
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count < 3)
            return double.NaN;
        var rx = Ranks(x);
        var ry = Ranks(y);
        double mx = rx.Average(), my = ry.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            sxy += (rx[i] - mx) * (ry[i] - my);
            sxx += (rx[i] - mx) * (rx[i] - mx);
            syy += (ry[i] - my) * (ry[i] - my);
        }

        return sxy / Math.Sqrt(sxx * syy);
    }

    // Ranks from 1, ties sharing their average.
    private static double[] Ranks(IReadOnlyList<double> values)
    {
        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Count];
        for (int start = 0; start < order.Length;)
        {
            int end = start + 1;
            while (end < order.Length && values[order[end]] == values[order[start]])
                end++;
            double rank = ((start + end - 1) / 2.0) + 1;
            for (int k = start; k < end; k++)
                ranks[order[k]] = rank;
            start = end;
        }

        return ranks;
    }

    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0)
            return double.NaN;
        double at = q * (sorted.Count - 1);
        int low = (int)Math.Floor(at);
        int high = Math.Min(low + 1, sorted.Count - 1);
        return sorted[low] + ((sorted[high] - sorted[low]) * (at - low));
    }

    public static int TierOf(double rank) => (int)Math.Floor(rank);

    /// <summary>One line of the summary: count, Spearman, exact, within one, mean absolute tier error, and high outliers.</summary>
    public static string Line(IReadOnlyList<Rated> rated)
    {
        if (rated.Count == 0)
            return "    -";
        double spearman = Spearman([.. rated.Select(r => r.Rank)], [.. rated.Select(r => (double)r.Part.Reported)]);
        double exact = rated.Count(r => TierOf(r.Rank) == r.Part.Reported) / (double)rated.Count;
        double within = rated.Count(r => Math.Abs(TierOf(r.Rank) - r.Part.Reported) <= 1) / (double)rated.Count;
        double mae = rated.Average(r => Math.Abs(TierOf(r.Rank) - r.Part.Reported));
        int high = rated.Count(r => r.Rank >= 7 && r.Part.Reported <= 5);
        return $"{rated.Count,5}  rho {spearman,5:0.000}  exact {exact,5:P0}  pm1 {within,5:P0}  MAE {mae,4:0.00}  high {high,3}";
    }

    /// <summary>The median rank, and the quartiles, for each reported tier from 0 to 6, and 7 and up.</summary>
    public static string Tiers(IReadOnlyList<Rated> rated)
    {
        var parts = new List<string>();
        for (int tier = 0; tier <= 7; tier++)
        {
            var ranks = rated.Where(r => tier < 7 ? r.Part.Reported == tier : r.Part.Reported >= 7).Select(r => r.Rank).Order().ToList();
            parts.Add(ranks.Count == 0 ? $"{tier}: -" : $"{tier}{(tier == 7 ? "+" : "")}: {Quantile(ranks, 0.5):0.0} [{Quantile(ranks, 0.25):0.0}-{Quantile(ranks, 0.75):0.0}] n{ranks.Count}");
        }

        return string.Join("  ", parts);
    }

    /// <summary>How many parts each attribute dominates the chart score of.</summary>
    public static string Dominance(IReadOnlyList<Rated> rated) =>
        string.Join("  ", DifficultyAttributes.All.Select(a => $"{a} {rated.Count(r => r.Dominant == a)}"));

    /// <summary>Each attribute's Spearman against the reported tier, and the median of each for each reported tier.</summary>
    public static IEnumerable<string> Attributes(IReadOnlyList<Rated> rated)
    {
        var reported = rated.Select(r => (double)r.Part.Reported).ToList();
        foreach (var attribute in DifficultyAttributes.All)
        {
            var values = rated.Select(r => r.Attributes[attribute]).ToList();
            var medians = Enumerable.Range(0, 8).Select(tier =>
            {
                var at = rated.Where(r => tier < 7 ? r.Part.Reported == tier : r.Part.Reported >= 7).Select(r => r.Attributes[attribute]).Order().ToList();
                return at.Count == 0 ? "   -" : $"{Quantile(at, 0.5),5:0}";
            });
            yield return $"    {attribute,-9} rho {Spearman(values, reported),5:0.000}  medians by tier {string.Join(" ", medians)}";
        }
    }
}
