namespace ChartShared.Audio.Synthesis;

/// <summary>
/// The beats of the calibration tool's test (charter-spec C11 item 1), in seconds on the app's monotonic clock:
/// beat <c>k</c> is at <see cref="FirstBeat"/> + <c>k</c> × <see cref="Period"/>. The flash, the note and the beep
/// all come from one of these, so the display and the audio share one schedule.
/// </summary>
/// <param name="FirstBeat">When beat 0 is, in seconds on the monotonic clock.</param>
/// <param name="Period">The time between beats, in seconds: 0.6 at the manual test's 100 BPM, 1 at the tap test's 60 BPM.</param>
/// <param name="BeatsPerBar">How many beats there are to a bar; the first of each is marked. 0 marks none, as in the tap test.</param>
/// <param name="Count">How many beats there are; the manual test's never end.</param>
public sealed record CalibrationBeats(double FirstBeat, double Period, int BeatsPerBar = 4, long Count = long.MaxValue)
{
    /// <summary>The manual test's tempo.</summary>
    public const double ManualPeriod = 0.6;

    /// <summary>The tap test's tempo: slow enough that a tap's nearest beat is never in doubt within ±500 ms.</summary>
    public const double TapPeriod = 1.0;

    /// <summary>When beat <paramref name="beat"/> is.</summary>
    public double TimeOf(long beat) => FirstBeat + (beat * Period);

    /// <summary>Whether <paramref name="beat"/> is the first of its bar, which is marked with a higher beep, a flash of another colour and a wider note.</summary>
    public bool IsMarked(long beat) => BeatsPerBar > 0 && ((beat % BeatsPerBar) + BeatsPerBar) % BeatsPerBar == 0;

    /// <summary>The last beat at or before <paramref name="time"/>, which is -1 or less before the first.</summary>
    public long BeatAtOrBefore(double time) => (long)Math.Floor((time - FirstBeat) / Period);

    /// <summary>Whether <paramref name="beat"/> is one of the beats: from 0, and fewer than <see cref="Count"/>.</summary>
    public bool Contains(long beat) => beat >= 0 && beat < Count;
}
