namespace ChartShared.Core.Charting;

/// <summary>
/// How fast a highway scrolls in <see cref="HighwayLengthMode.PixelsPerSecond"/>: how many pixels of it a second of the
/// song takes up, whatever its height. The one range, snapping and slider curve every highway uses, as
/// <see cref="HighwayLookAhead"/> is for the other mode.
/// </summary>
/// <remarks>The slider moves on a base-2 logarithmic scale (<see cref="ToScale"/>, <see cref="FromScale"/>), as the look-ahead's does.</remarks>
public static class HighwayPixelsPerSecond
{
    /// <summary>The slowest a highway goes by, in pixels per second.</summary>
    public const double Min = 25;

    /// <summary>The fastest a highway goes by, in pixels per second.</summary>
    public const double Max = 1000;

    /// <summary>The default for every highway, in pixels per second.</summary>
    public const double Default = 150;

    // Up to FineBelow a value is kept to 5, up to CoarseFrom to 10, and from there to 25: a finer grain where the
    // slider's scale spreads the values out.
    private const double FineBelow = 100;

    private const double CoarseFrom = 500;

    /// <summary>The slider's scale at <see cref="Min"/>.</summary>
    public static double MinScale => ToScale(Min);

    /// <summary>The slider's scale at <see cref="Max"/>.</summary>
    public static double MaxScale => ToScale(Max);

    /// <summary>
    /// <paramref name="pixelsPerSecond"/> as a speed a highway can have: within range and snapped; <paramref name="fallback"/>
    /// (also made so) when it isn't a number.
    /// </summary>
    public static double Clamp(double pixelsPerSecond, double fallback = Default) =>
        Snap(Math.Clamp(double.IsFinite(pixelsPerSecond) ? pixelsPerSecond : fallback, Min, Max));

    /// <summary><paramref name="pixelsPerSecond"/> rounded to the grain of its part of the range.</summary>
    public static double Snap(double pixelsPerSecond)
    {
        double grain = GrainAt(pixelsPerSecond);
        return Math.Clamp(Math.Round(pixelsPerSecond / grain, MidpointRounding.AwayFromZero) * grain, Min, Max);
    }

    /// <summary>
    /// <paramref name="pixelsPerSecond"/> moved by <paramref name="notches"/> of a key or a wheel, faster (a shorter
    /// highway) when positive: a notch is the grain where the speed is, so it always moves it.
    /// </summary>
    public static double Stepped(double pixelsPerSecond, int notches)
    {
        double value = Clamp(pixelsPerSecond);
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            // Going down, the grain is that of the part of the range being moved into, so 100 goes to 95 and not to 90.
            double grain = GrainAt(notches > 0 ? value : value - 1e-9);
            value = Clamp(value + (Math.Sign(notches) * grain));
        }

        return value;
    }

    private static double GrainAt(double pixelsPerSecond) => pixelsPerSecond < FineBelow ? 5 : pixelsPerSecond < CoarseFrom ? 10 : 25;

    /// <summary><paramref name="pixelsPerSecond"/> on the slider's scale.</summary>
    public static double ToScale(double pixelsPerSecond) => Math.Log2(pixelsPerSecond);

    /// <summary>The speed at <paramref name="scale"/> on the slider: within range and snapped.</summary>
    public static double FromScale(double scale) => Clamp(Math.Pow(2, scale));

    /// <summary>
    /// The seconds of song a highway shows from its strike line to its far end, <paramref name="pixelsAhead"/> long, at
    /// <paramref name="pixelsPerSecond"/>: what its look-ahead is, in this mode. There's no upper limit, as a tall highway at a low
    /// speed can show more than <see cref="HighwayLookAhead.Max"/>; a highway with no length yet shows the shortest look-ahead.
    /// </summary>
    public static double LookAheadFor(double pixelsAhead, double pixelsPerSecond)
    {
        double speed = Clamp(pixelsPerSecond);
        return double.IsFinite(pixelsAhead) && pixelsAhead > 0 ? Math.Max(HighwayLookAhead.ShortestShown, pixelsAhead / speed) : HighwayLookAhead.Min;
    }
}
