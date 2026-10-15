namespace ChartShared.Core.Charting.Editing;

/// <summary>Tidies a tempo found in the audio for the chart's starting BPM (charter-spec C1, Detect from audio).</summary>
public static class BpmTidy
{
    /// <summary>How near a whole BPM a tempo is taken to be it.</summary>
    public const double WholeWithin = 0.05;

    /// <summary><paramref name="bpm"/> as a whole BPM when it's within <see cref="WholeWithin"/> of one, else to three decimals.</summary>
    public static double Tidy(double bpm)
    {
        double whole = Math.Round(bpm, MidpointRounding.AwayFromZero);
        return Math.Abs(bpm - whole) <= WholeWithin ? whole : Math.Round(bpm, 3, MidpointRounding.AwayFromZero);
    }
}
