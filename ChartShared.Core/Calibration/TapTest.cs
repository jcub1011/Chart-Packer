namespace ChartShared.Core.Calibration;

/// <summary>One phase of the tap test: where its beats are, and when the user tapped, in seconds on one clock.</summary>
/// <param name="FirstBeat">When the phase's first beat is, on the taps' clock.</param>
/// <param name="BeatPeriod">The time between beats.</param>
/// <param name="Taps">When each tap was, in the order tapped.</param>
public sealed record TapPhase(double FirstBeat, double BeatPeriod, IReadOnlyList<double> Taps);

/// <summary>What the tap test measured.</summary>
/// <param name="LatencyMilliseconds">How much later the audio arrives than the display.</param>
/// <param name="SpreadMilliseconds">How scattered the taps were: the larger of the two phases' interquartile ranges.</param>
/// <param name="VisualKept">How many of the visual phase's taps were used.</param>
/// <param name="AudioKept">How many of the audio phase's taps were used.</param>
public sealed record TapEstimate(double LatencyMilliseconds, double SpreadMilliseconds, int VisualKept, int AudioKept);

/// <summary>
/// The calibration tool's tap test (charter-spec C11 item 2): the user taps along with flashes and no
/// sound, then with beeps and a blank screen. Their reaction time is the same in both phases, so the
/// difference between the two phases' errors is the audio's latency against the display.
/// </summary>
public static class TapTest
{
    /// <summary>How many taps at the start of each phase are ignored while the user finds the beat.</summary>
    public const int WarmUpTaps = 4;

    /// <summary>How many taps each phase must keep for an estimate.</summary>
    public const int MinimumKept = 4;

    /// <summary>A spread over this suggests trying again.</summary>
    public const double WideSpreadMilliseconds = 30;

    // Taps further than this many interquartile ranges from the median are dropped.
    private const double OutlierRanges = 1.5;

    /// <summary>
    /// The latency the taps show, or null when either phase has too few taps left to tell. Each tap's
    /// error is measured against its nearest beat; the warm-up taps and the taps far from the rest are
    /// dropped; the latency is the mean audio error less the mean visual error.
    /// </summary>
    /// <remarks>
    /// The mean of the taps kept, not their median as the spec first had it: with the outliers already
    /// dropped, about ten taps are left per phase, too few for a median to be within 20 ms 95% of the time
    /// at σ = 20 ms (92% in AC C11.2's test), where the mean is (96.5%).
    /// </remarks>
    public static TapEstimate? Estimate(TapPhase visual, TapPhase audio)
    {
        ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(audio);

        var visualErrors = KeptErrors(visual);
        var audioErrors = KeptErrors(audio);
        if (visualErrors.Length < MinimumKept || audioErrors.Length < MinimumKept)
            return null;

        double latency = audioErrors.Average() - visualErrors.Average();
        double spread = Math.Max(InterquartileRange(visualErrors), InterquartileRange(audioErrors));
        return new TapEstimate(latency * 1000, spread * 1000, visualErrors.Length, audioErrors.Length);
    }

    // The errors of the taps after the warm-up, against their nearest beats, without the outliers; sorted.
    private static double[] KeptErrors(TapPhase phase)
    {
        if (!(phase.BeatPeriod > 0) || !double.IsFinite(phase.BeatPeriod) || !double.IsFinite(phase.FirstBeat))
            throw new ArgumentOutOfRangeException(nameof(phase), "A phase needs a finite first beat and a positive beat period.");

        var errors = phase.Taps
            .Skip(WarmUpTaps)
            .Where(double.IsFinite)
            .Select(tap =>
            {
                double beat = phase.FirstBeat + (Math.Round((tap - phase.FirstBeat) / phase.BeatPeriod) * phase.BeatPeriod);
                return tap - beat;
            })
            .Order()
            .ToArray();
        if (errors.Length == 0)
            return errors;

        double median = Quantile(errors, 0.5);
        double limit = OutlierRanges * InterquartileRange(errors);
        return [.. errors.Where(e => Math.Abs(e - median) <= limit)];
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
