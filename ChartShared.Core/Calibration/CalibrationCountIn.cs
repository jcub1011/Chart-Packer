namespace ChartShared.Core.Calibration;

/// <summary>Which part of a calibration test a beat is in (<see cref="CalibrationCountIn"/>).</summary>
public enum CountInPhase
{
    /// <summary>Getting ready, before the count-in.</summary>
    GetReady,

    /// <summary>Counted in on screen, down to 1.</summary>
    CountIn,

    /// <summary>The notes or clicks to tap.</summary>
    Notes,
}

/// <summary>
/// Where a calibration test is: in <see cref="CountInPhase.CountIn"/>, the number shown; in
/// <see cref="CountInPhase.Notes"/>, which one is being tapped, from 1.
/// </summary>
public readonly record struct CountInStep(CountInPhase Phase, int Number);

/// <summary>
/// The count-in both calibration tests show (charter-spec C11 items 1 and 2): a bar to get ready, a bar counted down on
/// screen, then the notes or clicks, each from half a beat before it.
/// </summary>
public static class CalibrationCountIn
{
    /// <summary>Where a test is <paramref name="beats"/> beats after it started.</summary>
    /// <param name="readyBeats">How many beats it gets ready for.</param>
    /// <param name="leadBeats">How many beats come before the first note, getting ready included.</param>
    /// <param name="count">How many notes there are.</param>
    public static CountInStep At(double beats, int readyBeats, int leadBeats, int count)
    {
        if (beats < readyBeats)
            return new CountInStep(CountInPhase.GetReady, 0);
        if (beats < leadBeats)
            return new CountInStep(CountInPhase.CountIn, leadBeats - (int)Math.Floor(beats));
        return new CountInStep(CountInPhase.Notes, Math.Clamp((int)Math.Floor(beats - leadBeats + 0.5) + 1, 1, count));
    }
}
