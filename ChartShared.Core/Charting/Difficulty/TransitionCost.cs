using System.Numerics;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// What the fretting hand pays to move between lane states (docs/difficulty-model.md §4.2). Lane sets are bits, green
/// the lowest; 0 is an open note.
/// </summary>
public static class TransitionCost
{
    /// <summary>The hand's centre for <paramref name="lanes"/>: the average lane, or the open position for an open note (μ).</summary>
    public static double Centre(byte lanes, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (lanes == 0)
            return parameters.OpenPosition;
        int sum = 0;
        for (int lane = 0; lane < 5; lane++)
        {
            if ((lanes & (1 << lane)) != 0)
                sum += lane;
        }

        return (double)sum / BitOperations.PopCount(lanes);
    }

    /// <summary>How many lanes <paramref name="lanes"/> spans, lowest to highest (s); its count for one note or none.</summary>
    public static int Span(byte lanes)
    {
        int count = BitOperations.PopCount(lanes);
        if (count <= 1)
            return count;
        return BitOperations.Log2(lanes) - BitOperations.TrailingZeroCount(lanes) + 1;
    }

    /// <summary>The cost of landing the chord <paramref name="lanes"/>: its notes, its gaps, and any stretch past three lanes.</summary>
    public static double Shape(byte lanes, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        int count = BitOperations.PopCount(lanes);
        if (count <= 1)
            return 0;
        int span = Span(lanes);
        return (parameters.ChordNoteCost * count) + (parameters.ChordGapCost * (span - count)) + (parameters.ChordStretchCost * Math.Max(0, span - 3));
    }

    /// <summary>
    /// The cost of moving from <paramref name="from"/> (null for no note before) to <paramref name="to"/> (T): the lanes
    /// that change, going into or out of an open note, landing the new shape, and how far the hand's centre moves.
    /// </summary>
    public static double Between(byte? from, byte to, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (from is not { } previous)
            return Shape(to, parameters);
        if (previous == to)
            return 0;
        double cost = parameters.FingerCost * BitOperations.PopCount((uint)(previous ^ to));
        if (previous == 0 || to == 0)
            cost += parameters.OpenCost;
        return cost + Shape(to, parameters) + (parameters.TravelCost * Math.Abs(Centre(previous, parameters) - Centre(to, parameters)));
    }
}
