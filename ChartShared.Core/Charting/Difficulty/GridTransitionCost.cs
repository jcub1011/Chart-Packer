using System.Numerics;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// What the fretting hand pays to move between six-fret lane states (docs/difficulty-model.md §14.2), as
/// <see cref="TransitionCost"/> does for five frets. The frets are three columns, each a finger's: white frets are bits 0
/// to 2 and black ones 3 to 5, so a column's are bit c and bit c + 3. Both frets of a column are a barre, one finger
/// across them. 0 is an open note.
/// </summary>
public static class GridTransitionCost
{
    /// <summary>How many columns there are.</summary>
    public const int Columns = 3;

    /// <summary>What column <paramref name="column"/> of <paramref name="lanes"/> holds: 0 none, 1 white, 2 black, 3 both.</summary>
    public static int StateOf(byte lanes, int column) => ((lanes >> column) & 1) | (((lanes >> (column + Columns)) & 1) << 1);

    /// <summary>The columns <paramref name="lanes"/> has a fret in, as bits.</summary>
    public static int ColumnsOf(byte lanes) => (lanes | (lanes >> Columns)) & 0b111;

    /// <summary>The hand's centre for <paramref name="lanes"/>: the average column, or the open position for an open note.</summary>
    public static double Centre(byte lanes, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        int columns = ColumnsOf(lanes);
        if (columns == 0)
            return parameters.OpenPosition;
        int sum = 0;
        for (int column = 0; column < Columns; column++)
        {
            if ((columns & (1 << column)) != 0)
                sum += column;
        }

        return (double)sum / BitOperations.PopCount((uint)columns);
    }

    /// <summary>How many columns <paramref name="lanes"/> spans, lowest to highest; its column count for one or none.</summary>
    public static int Span(byte lanes)
    {
        int columns = ColumnsOf(lanes);
        int count = BitOperations.PopCount((uint)columns);
        if (count <= 1)
            return count;
        return BitOperations.Log2((uint)columns) - BitOperations.TrailingZeroCount(columns) + 1;
    }

    /// <summary>
    /// The cost of landing the chord <paramref name="lanes"/>: each column of it, each barre, white and black frets alone
    /// in different columns, and the columns it skips. None for a single fret or an open note.
    /// </summary>
    public static double Shape(byte lanes, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (BitOperations.PopCount(lanes) <= 1)
            return 0;
        int count = BitOperations.PopCount((uint)ColumnsOf(lanes));
        int barres = 0;
        bool white = false, black = false;
        for (int column = 0; column < Columns; column++)
        {
            switch (StateOf(lanes, column))
            {
                case 1:
                    white = true;
                    break;
                case 2:
                    black = true;
                    break;
                case 3:
                    barres++;
                    break;
            }
        }

        return (parameters.ChordNoteCost * count) + (parameters.BarreCost * barres) + (white && black ? parameters.MixedRowCost : 0)
            + (parameters.ChordGapCost * (Span(lanes) - count));
    }

    /// <summary>
    /// The cost of moving from <paramref name="from"/> (null for no note before) to <paramref name="to"/>: each column whose
    /// finger changes, less for one that moves between its white and black fret, going into or out of an open note,
    /// landing the new shape, and how far the hand's centre moves, in columns.
    /// </summary>
    public static double Between(byte? from, byte to, DifficultyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (from is not { } previous)
            return Shape(to, parameters);
        if (previous == to)
            return 0;
        double cost = 0;
        for (int column = 0; column < Columns; column++)
        {
            int a = StateOf(previous, column), b = StateOf(to, column);
            if (a != b)
                cost += (a, b) is (1, 2) or (2, 1) ? parameters.GridRowCost : parameters.FingerCost;
        }

        if (previous == 0 || to == 0)
            cost += parameters.OpenCost;
        return cost + Shape(to, parameters) + (parameters.TravelCost * Math.Abs(Centre(previous, parameters) - Centre(to, parameters)));
    }
}
