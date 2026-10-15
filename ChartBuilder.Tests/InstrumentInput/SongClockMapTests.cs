using ChartPacker.Audio.Playback;
using ChartPacker.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Inputs placed in the song by when they happened, not when they're handled (charter-spec C19 item 3).</summary>
[TestClass]
public sealed class SongClockMapTests
{
    [TestMethod]
    public void AnInputBetweenFramesRunsOnFromTheFrameBeforeItAtTheSpeed()
    {
        var map = new SongClockMap();
        map.Sample(100.000, 10.0, 0.5, playing: true);
        map.Sample(100.016, 10.008, 0.5, playing: true);

        Assert.AreEqual(10.012, map.SongAt(100.024)!.Value, 1e-9);
        Assert.AreEqual(10.004, map.SongAt(100.008)!.Value, 1e-9);
    }

    [TestMethod]
    public void AnInputWhilePausedOrBeforeTheFirstFrameIsInNoSong()
    {
        var map = new SongClockMap();
        map.Sample(100.0, 10.0, 1, playing: false);
        map.Sample(101.0, 10.0, 1, playing: true);

        Assert.IsNull(map.SongAt(100.5));
        Assert.IsNull(map.SongAt(99.0));
        Assert.IsNotNull(map.SongAt(101.1));
    }

    [TestMethod]
    public void AnInputBeforeTheLoopJumpsBackIsPlacedFromBeforeIt()
    {
        var map = new SongClockMap();
        map.Sample(100.000, 20.000, 1, playing: true);
        map.Sample(100.016, 20.016, 1, playing: true);
        Assert.IsFalse(map.Jumped);
        map.Sample(100.032, 12.000, 1, playing: true);
        Assert.IsTrue(map.Jumped);

        // Handled after the jump, but it happened before it.
        Assert.AreEqual(20.026, map.SongAt(100.026)!.Value, 1e-9);
        Assert.AreEqual(12.004, map.SongAt(100.036)!.Value, 1e-9);
    }

    /// <summary>
    /// AC C19.1: an input whose own time is exactly on a note, handled up to 100 ms later, is judged 0 ms off at 100% and
    /// at 50% speed, with a latency of 40 ms, the song clock being <c>SmoothPosition − latency × speed</c>.
    /// </summary>
    [TestMethod]
    [DataRow(1.0)]
    [DataRow(0.5)]
    public void AnInputOnANoteIsOnItHoweverLateItsHandled(double speed)
    {
        const int latencyMs = 40;
        const double note = 12.0;
        var map = new SongClockMap();

        // The audio's position, run on from 10 s at the speed; the song as drawn is that less the latency × speed.
        double smoothAt(double t) => 10 + ((t - 100) * speed);
        double drawn(double t) => LatencyCompensation.Drawn(TimeSpan.FromSeconds(smoothAt(t)), latencyMs, speed).TotalSeconds;
        double onNote = 100 + ((note - 10 + (latencyMs / 1000.0 * speed)) / speed);

        for (double t = 100; t <= onNote + 0.1; t += 1 / 60.0)
            map.Sample(t, drawn(t), speed, playing: true);

        var input = new ActionEvent(InstrumentAction.Green, true, onNote, InputDeviceId.Keyboard);
        Assert.AreEqual(note, map.Map(input)!.Value.Song, 1e-6);
    }
}
