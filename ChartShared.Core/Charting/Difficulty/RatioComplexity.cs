namespace ChartShared.Core.Charting.Difficulty;

/// <summary>Where a note falls within its beat (docs/difficulty-model.md §4.3).</summary>
public enum SubdivisionFamily : byte
{
    /// <summary>On the beat or the half-beat, which belong to every feel.</summary>
    Neutral,

    /// <summary>On a straight subdivision: sixteenths and finer, down to sixty-fourths.</summary>
    Binary,

    /// <summary>On a triplet subdivision: eighth triplets and finer, down to sixteenth triplets.</summary>
    Ternary,

    /// <summary>On neither grid: quintuplets, septuplets, or unquantized notes.</summary>
    Other,
}

/// <summary>How hard the timing of notes is to read (docs/difficulty-model.md §4.3).</summary>
public static class RatioComplexity
{
    // The ratios between common note values, as fractions, and their Tenney heights.
    private static readonly (double Ratio, double Height)[] CleanRatios =
        [.. new (int P, int Q)[] { (1, 1), (4, 3), (3, 2), (2, 1), (3, 1), (4, 1), (6, 1), (8, 1) }.Select(static r => ((double)r.P / r.Q, TenneyHeight(r.P, r.Q)))];

    /// <summary>The ratios <see cref="Cost"/> matches against (𝒬): those between common note values.</summary>
    public static IReadOnlyList<double> Clean { get; } = [.. CleanRatios.Select(static r => r.Ratio)];

    /// <summary>The Tenney height of <paramref name="p"/>/<paramref name="q"/> in lowest terms: log₂(pq).</summary>
    public static double TenneyHeight(int p, int q) => Math.Log2((double)p * q);

    /// <summary>
    /// How complex the ratio <paramref name="ratio"/> (1 or more) between two intervals is (Ψ): the cheapest clean ratio's
    /// Tenney height, plus a cost for how far off it the ratio is past the dead zone.
    /// </summary>
    public static double Cost(double ratio, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        double log = Math.Log2(ratio);
        double best = double.PositiveInfinity;
        foreach (var (clean, height) in CleanRatios)
        {
            double off = Math.Max(0, Math.Abs(log - Math.Log2(clean)) - parameters.RatioDeadZone);
            best = Math.Min(best, height + (parameters.RatioOffCost * off));
        }

        return best;
    }

    /// <summary>
    /// The cost of the change from <paramref name="before"/> to <paramref name="interval"/> (Ψ′): nothing after a rest, or
    /// with no interval before; otherwise <see cref="Cost"/>, capped.
    /// </summary>
    public static double ChangeCost(double interval, double before, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        double longer = Math.Max(interval, before);
        if (double.IsInfinity(longer) || longer > parameters.RestMs)
            return 0;
        double shorter = Math.Max(Math.Min(interval, before), double.Epsilon);
        return Math.Min(Cost(longer / shorter, parameters), parameters.MaxRatioCost);
    }

    /// <summary>The distance from <paramref name="x"/> to the nearest multiple of 1/<paramref name="m"/> (⟨x⟩ₘ).</summary>
    public static double GridDistance(double x, int m)
    {
        double scaled = x * m;
        return Math.Abs(scaled - Math.Round(scaled)) / m;
    }

    /// <summary>Which subdivision family a note at <paramref name="beat"/> belongs to (φ), checked in order.</summary>
    public static SubdivisionFamily FamilyOf(double beat, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        double within = beat - Math.Floor(beat);
        double tolerance = parameters.BeatTolerance;
        if (GridDistance(within, 2) < tolerance)
            return SubdivisionFamily.Neutral;
        if (GridDistance(within, 16) < tolerance)
            return SubdivisionFamily.Binary;
        if (GridDistance(within, 12) < tolerance)
            return SubdivisionFamily.Ternary;
        return SubdivisionFamily.Other;
    }
}
