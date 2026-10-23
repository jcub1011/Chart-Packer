namespace ChartShared.Core.Calibration;

/// <summary>What the tap test measured.</summary>
/// <param name="OffsetMilliseconds">How much later than the notes the taps came, on average: positive when the device reports late.</param>
/// <param name="SpreadMilliseconds">How scattered the taps were: their interquartile range.</param>
/// <param name="Kept">How many of the taps were used.</param>
public sealed record TapEstimate(double OffsetMilliseconds, double SpreadMilliseconds, int Kept);

/// <summary>
/// The calibration tool's input-device test (charter-spec C11 item 2): the user hits a device's buttons in time with notes
/// reaching the strike line, with no sound. The taps are timed on the clock the notes are drawn on, so what's left is how
/// much later than the screen shows a note the device reports the hit: its own lag, the display's, and the player's habit.
/// </summary>
public static class TapTest
{
    /// <summary>How many taps must be kept for an estimate.</summary>
    public const int MinimumKept = 8;

    /// <summary>A spread over this suggests trying again.</summary>
    public const double WideSpreadMilliseconds = 30;

    // Taps further than this many interquartile ranges from the median are dropped, but never those within 5 ms of it, as
    // when the taps are so even their range is all but 0.
    private const double OutlierRanges = 1.5;
    private const double OutlierFloor = 0.005;

    /// <summary>
    /// The offset the taps show, or null when too few are left to tell. Each tap is measured against its nearest note, and
    /// taps more than half the gap between notes from any (as in the lead-in) are ignored. Only the closest tap to each
    /// note is used, then the taps far from the rest are dropped; the offset is the mean of what's left.
    /// </summary>
    /// <param name="notes">When the notes reach the strike line, in order, in seconds.</param>
    /// <param name="taps">When the device reported each tap, on the same clock.</param>
    /// <remarks>
    /// The mean of the taps kept, not their median: with the outliers dropped, about a dozen are left, too few for a median
    /// to be within 20 ms 95% of the time at σ = 20 ms, where the mean is (AC C11.2).
    /// </remarks>
    public static TapEstimate? Estimate(IReadOnlyList<double> notes, IReadOnlyList<double> taps)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(taps);
        if (notes.Count == 0)
            return null;

        double reach = ReachOf(notes);
        var closest = new double?[notes.Count];
        foreach (double tap in taps)
        {
            if (!double.IsFinite(tap))
                continue;

            int note = NearestNote(notes, tap);
            double error = tap - notes[note];
            if (Math.Abs(error) <= reach && (closest[note] is not { } kept || Math.Abs(error) < Math.Abs(kept)))
                closest[note] = error;
        }

        double[] errors = [.. closest.OfType<double>().Order()];
        if (errors.Length == 0)
            return null;

        double median = Quantile(errors, 0.5);
        double limit = Math.Max(OutlierFloor, OutlierRanges * InterquartileRange(errors));
        errors = [.. errors.Where(e => Math.Abs(e - median) <= limit)];
        if (errors.Length < MinimumKept)
            return null;

        return new TapEstimate(errors.Average() * 1000, InterquartileRange(errors) * 1000, errors.Length);
    }

    // Half the smallest gap between notes, so a tap is only ever counted against one; half a second for a lone note.
    private static double ReachOf(IReadOnlyList<double> notes)
    {
        double gap = double.PositiveInfinity;
        for (int i = 1; i < notes.Count; i++)
            gap = Math.Min(gap, notes[i] - notes[i - 1]);
        return double.IsFinite(gap) && gap > 0 ? gap / 2 : 0.5;
    }

    private static int NearestNote(IReadOnlyList<double> notes, double tap)
    {
        int nearest = 0;
        for (int i = 1; i < notes.Count; i++)
        {
            if (Math.Abs(tap - notes[i]) < Math.Abs(tap - notes[nearest]))
                nearest = i;
        }

        return nearest;
    }

    private static double InterquartileRange(double[] sorted) => Quantile(sorted, 0.75) - Quantile(sorted, 0.25);

    // The q quantile of sorted values, interpolating between the two nearest.
    private static double Quantile(double[] sorted, double q)
    {
        double at = q * (sorted.Length - 1);
        int below = (int)Math.Floor(at);
        int above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + ((at - below) * (sorted[above] - sorted[below]));
    }
}
