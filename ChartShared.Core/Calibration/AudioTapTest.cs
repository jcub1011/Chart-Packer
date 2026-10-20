namespace ChartShared.Core.Calibration;

/// <summary>
/// The calibration tool's tap test for the audio/visual latency (charter-spec C11 item 1): the user taps along with
/// clicks of its own, with the notes hidden, so the taps follow what's heard. After a quiet bar to get ready and a bar
/// counted in on screen, still quiet, at the clicks' tempo, <see cref="ClickCount"/> clicks are tapped, the first on the
/// beat after the count. Each tap is placed on the song's timeline, the one the clicks
/// are scheduled on, less its device's own offset (<see cref="TapTest"/>, measured against what's seen), which takes off
/// the device's lag and the player's habit and puts back the screen's. What's left is how much later the audio is heard
/// than the screen shows it: the latency.
/// </summary>
public static class AudioTapTest
{
    /// <summary>How many beats are quiet, to get ready, before the count-in: a bar.</summary>
    public const int ReadyBeats = 4;

    /// <summary>How many beats are counted in on screen, with no click, before the first click: a bar.</summary>
    public const int CountInBeats = 4;

    /// <summary>How many clicks are tapped.</summary>
    public const int ClickCount = 16;

    // A beat is 750 ms, so a tap can only be told apart from the next click's within one. The window is put off-centre,
    // from 250 ms before a click to 500 ms after it: Bluetooth can be most of 500 ms late, and the screen is rarely
    // much later than the audio.
    private const double WindowCentreSeconds = 0.125;

    /// <summary>When the count-in's first beat is, in seconds of song, for a test started at <paramref name="from"/>.</summary>
    public static double CountInStart(TimeSpan from) => from.TotalSeconds + (ReadyBeats * CalibrationChart.BeatSeconds);

    /// <summary>
    /// When the clicks to tap are, in seconds of song, for a test started at <paramref name="from"/>: a beat apart, from
    /// the beat after the count-in.
    /// </summary>
    public static double[] ClickTimes(TimeSpan from)
    {
        double beat = CalibrationChart.BeatSeconds;
        double first = CountInStart(from) + (CountInBeats * beat);
        var times = new double[ClickCount];
        for (int i = 0; i < ClickCount; i++)
            times[i] = first + (i * beat);
        return times;
    }

    /// <summary>When the test ends, in seconds of song: a beat after the last click, for a late last tap.</summary>
    public static double EndSeconds(IReadOnlyList<double> clicks)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        return clicks[^1] + CalibrationChart.BeatSeconds;
    }

    /// <summary>
    /// The latency the taps show, or null when too few are left to tell, scored as <see cref="TapTest.Estimate"/> scores
    /// them. A tap counts against the click from 250 ms before it to 500 ms after it, so taps with the count-in don't count.
    /// </summary>
    /// <param name="clicks">When the clicks are, from <see cref="ClickTimes"/>.</param>
    /// <param name="taps">When each tap came, in seconds of song, less its device's offset.</param>
    public static TapEstimate? Estimate(IReadOnlyList<double> clicks, IReadOnlyList<double> taps)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        ArgumentNullException.ThrowIfNull(taps);
        return TapTest.Estimate(clicks, [.. taps.Select(t => t - WindowCentreSeconds)]) is { } estimate
            ? estimate with { OffsetMilliseconds = estimate.OffsetMilliseconds + (WindowCentreSeconds * 1000) }
            : null;
    }
}
