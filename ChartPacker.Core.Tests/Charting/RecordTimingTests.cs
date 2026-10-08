using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charting;

/// <summary>
/// Record mode's timing, from the playback clock to the tick a press lands on (charter-spec C15 item 5, AC C15.3). Here,
/// where the audio library is too: a press is timed by the editor's clock, <see cref="LatencyCompensation.Drawn"/> of
/// <c>SmoothPosition</c> when the key is handled, then quantised.
/// </summary>
[TestClass]
public sealed class RecordTimingTests
{
    private static readonly ChartTrackKey GuitarExpert = new(ChartInstrument.Guitar, ChartDifficulty.Expert);

    [TestMethod]
    [DataRow(1.0, 40)]
    [DataRow(0.5, 20)]
    [DataRow(0.75, 30)]
    public void AKeyPressedWhereTheDisplayShowsTheBeatIsPlacedOnTheBeat(double speed, int pastMs)
    {
        // AC C15.3: with a latency of 40 ms, a key pressed when SmoothPosition is 40 ms past a beat is on the beat as it's
        // shown and heard; at 50%, the same holds 20 ms of song time past it. At 120 BPM and 480 ticks a beat, tick 960
        // is 1 s. Snap is off, so the tick is the clock's to the tick, not the grid's.
        var session = Session();
        session.Snap = session.Snap with { IsOn = false };
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        var drawn = LatencyCompensation.Drawn(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(pastMs), 40, speed);
        pass.Press(0, drawn.TotalSeconds);
        pass.Finish(2);

        Assert.AreEqual(960, Notes(session).Single().Tick);
    }

    [TestMethod]
    public void PressesAFewMillisecondsOffStillLandOnTheGrid()
    {
        // The handling of a key event comes a little after the press; quantising to the grid takes it up.
        var session = Session();
        session.Snap = SnapGrid.Of(16);
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);
        var random = new Random(4);
        for (int beat = 1; beat <= 16; beat++)
        {
            double jitter = (random.NextDouble() * 10) - 5;
            var drawn = LatencyCompensation.Drawn(TimeSpan.FromSeconds(beat * 0.5) + TimeSpan.FromMilliseconds(40 + jitter), 40, 1);
            pass.Press(beat % 5, drawn.TotalSeconds);
            pass.Release(beat % 5, drawn.TotalSeconds + 0.01);
        }

        pass.Finish(10);

        Assert.AreSequenceEqual([.. Enumerable.Range(1, 16).Select(b => b * 480L)], Notes(session).Select(n => n.Tick).ToList());
    }

    private static EditorSession Session()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        return new EditorSession(new ChartUndoStack(chart), TimeSpan.Zero, []) { Focused = GuitarExpert };
    }

    private static IReadOnlyList<EditableNote> Notes(EditorSession session) => session.TrackOf(GuitarExpert)!.Notes;
}
