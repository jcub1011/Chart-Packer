using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Synthesis;
using static ChartPacker.Core.Tests.Audio.TestAudio;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>The calibration tool's beeps, in what a <see cref="StemPlayer"/> of a silent mix writes to its output.</summary>
[TestClass]
public sealed class CalibrationScheduleTests
{
    private const int Rate = 48000;

    // The first beat comes this long after the anchor, which is where the player starts.
    private const double Lead = 0.5;

    /// <summary>
    /// AC C11.1: with the latency at +100 ms, the beeps written start 100 ms earlier against the flash schedule than at
    /// 0 ms, to within a frame, and the flash schedule is the same.
    /// </summary>
    [TestMethod]
    public void ALatencyWritesTheBeepsThatMuchEarlier()
    {
        var atZero = Play(latency: 0, seconds: 2.5);
        var atHundred = Play(latency: 0.1, seconds: 2.5);

        var onsetsAtZero = Onsets(atZero.Written);
        var onsetsAtHundred = Onsets(atHundred.Written);

        Assert.HasCount(4, onsetsAtZero);
        Assert.HasCount(onsetsAtZero.Count, onsetsAtHundred);
        for (int n = 0; n < onsetsAtZero.Count; n++)
            Assert.AreEqual(Rate / 10, onsetsAtZero[n] - onsetsAtHundred[n], 1, $"Beep {n}.");
        Assert.AreEqual(atZero.Beats, atHundred.Beats);
    }

    /// <summary>
    /// AC C11.4: with a latency of 400 ms, each marked (2 kHz) beep is written 400 ms before the marked beat it belongs
    /// to, so it can only be lined up with a marked flash.
    /// </summary>
    [TestMethod]
    public void AMarkedBeepIsWrittenBeforeItsOwnMarkedBeat()
    {
        var played = Play(latency: 0.4, seconds: 5.3);

        var onsets = Onsets(played.Written);
        Assert.HasCount(9, onsets);
        for (int beat = 0; beat < onsets.Count; beat++)
        {
            // Where the flash for this beat is, in frames of the mix: the anchor was at the mix's start.
            double flash = (played.Beats.TimeOf(beat) - played.AnchorClock) * Rate;
            Assert.AreEqual(flash - (0.4 * Rate), onsets[beat], 1, $"Beep {beat}.");
            Assert.AreEqual(played.Beats.IsMarked(beat), IsHigh(played.Written, onsets[beat]), $"Beep {beat}.");
        }
        Assert.IsTrue(IsHigh(played.Written, onsets[4]));
    }

    [TestMethod]
    public void AScheduleOfSixteenBeatsStopsThere()
    {
        var beep = Synth.CalibrationBeep(marked: false, Rate);
        var schedule = new CalibrationSchedule(
            new CalibrationBeats(FirstBeat: 1, Period: 1, Count: 16), anchorClock: 0, anchorSong: 0, latency: 0, beep, beep);

        var sounds = new List<ScheduledSound>();
        schedule.SoundsBetween(0, 60L * Rate, sounds);

        Assert.HasCount(16, sounds);
        Assert.AreEqual(Rate, sounds[0].SourceFrame);
        Assert.AreEqual(16L * Rate, sounds[^1].SourceFrame);
    }

    [TestMethod]
    public void EachBeepIsGivenOnceAcrossChunks()
    {
        var beep = Synth.CalibrationBeep(marked: false, Rate);
        var schedule = new CalibrationSchedule(
            new CalibrationBeats(FirstBeat: 10.3, Period: 0.6), anchorClock: 10, anchorSong: 2, latency: 0.25, beep, beep);

        var sounds = new List<ScheduledSound>();
        for (long from = 0; from < 10L * Rate; from += 960)
            schedule.SoundsBetween(from, from + 960, sounds);

        var frames = sounds.Select(s => s.SourceFrame).ToList();
        CollectionAssert.AllItemsAreUnique(frames);
        Assert.AreEqual(schedule.FrameOf(0), frames[0]);
        Assert.AreEqual((long)Math.Round((2 + 0.3 - 0.25) * Rate), frames[0]);
        Assert.AreSequenceEqual(Enumerable.Range(0, frames.Count).Select(k => schedule.FrameOf(k)).ToList(), frames);
    }

    [TestMethod]
    public void ABeepGivenIsNotGivenAgainByTheNextSchedule()
    {
        var beep = Synth.CalibrationBeep(marked: false, Rate);
        var beats = new CalibrationBeats(FirstBeat: 1, Period: 0.6);
        var given = new CalibrationBeepsGiven();
        var first = new CalibrationSchedule(beats, anchorClock: 0, anchorSong: 0, latency: 0, beep, beep, given);
        // Re-anchored 1 ms later, which moves beat 1 (at 1.6 s) from just before the handover to just after it.
        var second = new CalibrationSchedule(beats, anchorClock: 0, anchorSong: 0.001, latency: 0, beep, beep, given);

        var sounds = new List<ScheduledSound>();
        long handover = (long)(1.6005 * Rate);
        first.SoundsBetween(0, handover, sounds);
        second.SoundsBetween(handover, 3L * Rate, sounds);

        Assert.HasCount(4, sounds); // Beats 0 to 3, at 1, 1.6, 2.2 and 2.8 s.
        Assert.AreEqual(3, given.Last);
    }

    [TestMethod]
    public void TheTapTestsBeatsAreNotMarked()
    {
        var beats = new CalibrationBeats(FirstBeat: 0, Period: CalibrationBeats.TapPeriod, BeatsPerBar: 0, Count: 16);

        Assert.DoesNotContain(b => beats.IsMarked(b), Enumerable.Range(0, 16));
    }

    private sealed record Played(float[] Written, CalibrationBeats Beats, double AnchorClock);

    // Plays a silent mix with the beeps laid over it, anchored where it starts, and gives what was written.
    private static Played Play(double latency, double seconds)
    {
        var clock = new ManualClock();
        var output = new FakeOutput(Rate);
        var mixer = new StemMixer([], Rate) { MinimumLengthFrames = 60L * Rate };
        using var player = new StemPlayer(mixer, output, clock);

        double anchorClock = (double)clock.GetTimestamp() / clock.TimestampFrequency;
        var beats = new CalibrationBeats(anchorClock + Lead, CalibrationBeats.ManualPeriod);
        player.Sounds = new CalibrationSchedule(
            beats,
            anchorClock,
            player.SmoothPosition.TotalSeconds,
            latency,
            Synth.CalibrationBeep(marked: false, Rate),
            Synth.CalibrationBeep(marked: true, Rate));

        player.Play();
        PlayFor(output, (int)(seconds * Rate));
        player.Pause();
        return new Played(output.WrittenLeft()[..(int)(seconds * Rate)], beats, anchorClock);
    }

    // The frames each beep starts at: the first sound after a silence.
    private static List<int> Onsets(float[] written)
    {
        var onsets = new List<int>();
        int lastSound = -Rate;
        for (int n = 0; n < written.Length; n++)
        {
            if (written[n] == 0)
                continue;
            if (n - lastSound > Rate / 10)
                onsets.Add(n - 1); // A beep's first sample is 0, faded in from silence.
            lastSound = n;
        }

        return onsets;
    }

    // Whether the beep starting at `onset` is the 2 kHz one, by how often it crosses zero over its 30 ms.
    private static bool IsHigh(float[] written, int onset)
    {
        int crossings = 0;
        int end = Math.Min(written.Length - 1, onset + (Rate * 30 / 1000));
        for (int n = onset + 1; n < end; n++)
        {
            if (Math.Sign(written[n]) != Math.Sign(written[n + 1]) && written[n + 1] != 0)
                crossings++;
        }

        return crossings > 90; // About 60 at 1 kHz, and 120 at 2 kHz.
    }
}
