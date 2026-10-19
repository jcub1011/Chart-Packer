using ChartShared.Core.Charts;

namespace ChartShared.Core.Calibration;

/// <summary>
/// The chart the calibration tool plays (charter-spec C11 item 1): 80 BPM in 4/4, with an open note on every beat,
/// drawn on the real highway and clicked by the metronome.
/// </summary>
public static class CalibrationChart
{
    /// <summary>Ticks per beat.</summary>
    public const int Resolution = 192;

    /// <summary>80 BPM: slow enough to judge each note against its click.</summary>
    public const long MicrosecondsPerQuarter = 750_000;

    /// <summary>80 BPM in 4/4.</summary>
    public static TempoMap Tempo { get; } = new(
        Resolution,
        [new TempoChange(0, MicrosecondsPerQuarter)],
        [new TimeSignature(0, 4, 4)]);

    /// <summary>The time between beats, in seconds.</summary>
    public static double BeatSeconds => MicrosecondsPerQuarter / 1_000_000.0;

    /// <summary>How many beats start within <paramref name="length"/> of the first, the first included.</summary>
    public static int BeatsIn(TimeSpan length) =>
        length < TimeSpan.Zero ? 0 : (int)Math.Floor(length.TotalSeconds / BeatSeconds) + 1;

    /// <summary>How many beats the input-device test leads in with, from song 0, before its first note: two bars.</summary>
    public const int DeviceLeadBeats = 8;

    /// <summary>How many notes the input-device test has.</summary>
    public const int DeviceNoteCount = 16;

    /// <summary>When the input-device test's notes reach the strike line, in seconds from its start.</summary>
    public static double[] DeviceNoteTimes()
    {
        var times = new double[DeviceNoteCount];
        for (int i = 0; i < DeviceNoteCount; i++)
            times[i] = (DeviceLeadBeats + i) * BeatSeconds;
        return times;
    }

    /// <summary>When the input-device test ends, in seconds from its start: a beat after its last note, for a late last tap.</summary>
    public static double DeviceEndSeconds => (DeviceLeadBeats + DeviceNoteCount) * BeatSeconds;

    /// <summary>An open note on each of <paramref name="count"/> beats, from beat <paramref name="first"/>.</summary>
    public static ChartNote[] Notes(int count, int first = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(first);

        var notes = new ChartNote[count];
        for (int i = 0; i < count; i++)
            notes[i] = new ChartNote((long)(first + i) * Resolution, 0, ChartNote.OpenLane, NoteFlags.None);
        return notes;
    }
}
