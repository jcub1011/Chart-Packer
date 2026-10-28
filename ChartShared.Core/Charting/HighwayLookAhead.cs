namespace ChartShared.Core.Charting;

/// <summary>
/// How far ahead of its strike line a highway shows, in seconds (its look-ahead, the "seek ahead" of the settings): the
/// one range, snapping and slider curve every highway uses, so what's saved in either app's settings means the same
/// wherever it's shown.
/// </summary>
/// <remarks>
/// A slider moves on a base-2 logarithmic scale (<see cref="ToScale"/>, <see cref="FromScale"/>), so the same travel is
/// the same ratio and the short look-aheads, the ones most often wanted, are as easy to set as the long ones.
/// </remarks>
public static class HighwayLookAhead
{
    /// <summary>The shortest look-ahead, in seconds.</summary>
    public const double Min = 0.5;

    /// <summary>The longest look-ahead, in seconds.</summary>
    public const double Max = 60;

    /// <summary>The step a key or a wheel notch moves a look-ahead by, in seconds.</summary>
    public const double Step = 0.5;

    // Below this a look-ahead is kept to a tenth of a second, below CoarseFrom to half a second, and from there to a whole
    // one; a finer grain where the slider's scale spreads the values out.
    private const double FineBelow = 2;

    private const double CoarseFrom = 10;

    /// <summary>
    /// How far ahead, in seconds, a highway shows before one has been chosen: what it showed at its normal note speed, from
    /// when it was set by a note speed rather than its look-ahead.
    /// </summary>
    public const double Default = 1.4;

    /// <summary>The slider's scale at <see cref="Min"/>.</summary>
    public static double MinScale => ToScale(Min);

    /// <summary>The slider's scale at <see cref="Max"/>.</summary>
    public static double MaxScale => ToScale(Max);

    /// <summary>
    /// <paramref name="seconds"/> as a look-ahead a highway can have: within range and snapped; <paramref name="fallback"/>
    /// (also made so) when it isn't a number.
    /// </summary>
    public static double Clamp(double seconds, double fallback = Min) =>
        Snap(Math.Clamp(double.IsFinite(seconds) ? seconds : fallback, Min, Max));

    /// <summary>
    /// The look-ahead of a highway set by a note speed, a multiple of the normal one, as Chart Packer once saved it:
    /// <paramref name="fallback"/> when <paramref name="speed"/> isn't a positive number.
    /// </summary>
    public static double FromNoteSpeed(double speed, double fallback) =>
        double.IsFinite(speed) && speed > 0 ? Clamp(Default / speed, fallback) : Clamp(fallback);

    /// <summary><paramref name="seconds"/> rounded to the grain of its part of the range, a tenth of a second up to half a second to a second.</summary>
    public static double Snap(double seconds)
    {
        double grain = GrainAt(seconds);
        return Math.Round(Math.Round(seconds / grain, MidpointRounding.AwayFromZero) * grain, 1);
    }

    /// <summary>
    /// <paramref name="seconds"/> moved by <paramref name="notches"/> of a key or a wheel, later when positive: a notch is
    /// <see cref="Step"/>, or the grain where the look-ahead is when that's coarser, so a notch always moves it
    /// (a coarser grain would snap it back to where it was).
    /// </summary>
    public static double Stepped(double seconds, int notches)
    {
        double value = Clamp(seconds);
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            // Going down, the grain is that of the part of the range being moved into, so 10 goes to 9.5 and not to 9.
            double grain = Math.Max(Step, GrainAt(notches > 0 ? value : value - 1e-9));
            value = Clamp(value + Math.Sign(notches) * grain);
        }

        return value;
    }

    private static double GrainAt(double seconds) => seconds < FineBelow ? 0.1 : seconds < CoarseFrom ? 0.5 : 1;

    /// <summary><paramref name="seconds"/> on the slider's scale.</summary>
    public static double ToScale(double seconds) => Math.Log2(seconds);

    /// <summary>The look-ahead, in seconds, at <paramref name="scale"/> on the slider: within range and snapped.</summary>
    public static double FromScale(double scale) => Clamp(Math.Pow(2, scale));
}
