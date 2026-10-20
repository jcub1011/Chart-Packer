namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// Turns a difficulty score into a rank (docs/difficulty-model.md §7): 0 to 6 for charts written to be played, 6 the soft
/// cap the hardest of them reach, and 7 and up, without end, for overcharts. Piecewise linear between the thresholds, and
/// past the soft cap each rank as wide as rank 6.
/// </summary>
public static class RankCurve
{
    /// <summary>The first rank past the soft cap: an overchart.</summary>
    public const int FirstOverchart = 7;

    /// <summary>The rank of <paramref name="score"/>, continuous: 5.42 is a little under halfway through rank 5.</summary>
    public static double Continuous(double score, DifficultyParameters? parameters = null)
    {
        var thresholds = (parameters ?? DifficultyParameters.Default).RankThresholds;
        if (score <= thresholds[0])
            return 0;
        int last = thresholds.Count - 1;
        for (int rank = 0; rank < last; rank++)
        {
            if (score < thresholds[rank + 1])
                return rank + ((score - thresholds[rank]) / (thresholds[rank + 1] - thresholds[rank]));
        }

        return last + ((score - thresholds[last]) / Width(parameters));
    }

    /// <summary>The whole rank of <paramref name="score"/>: 0 and up, 7 and up for an overchart.</summary>
    public static int Tier(double score, DifficultyParameters? parameters = null) => (int)Math.Floor(Continuous(score, parameters));

    /// <summary>The score <paramref name="rank"/> starts at, past the soft cap too.</summary>
    public static double ThresholdOf(int rank, DifficultyParameters? parameters = null)
    {
        var thresholds = (parameters ?? DifficultyParameters.Default).RankThresholds;
        int last = thresholds.Count - 1;
        return rank <= last ? thresholds[Math.Max(0, rank)] : thresholds[last] + ((rank - last) * Width(parameters));
    }

    // How wide the last realistic rank is, which each rank past it is too.
    private static double Width(DifficultyParameters? parameters)
    {
        var p = parameters ?? DifficultyParameters.Default;
        return p.SoftCap - p.RankThresholds[^1];
    }
}
