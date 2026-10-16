using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class TempoMapTests
{
    // 120 BPM for four beats, then 60 BPM.
    private static readonly TempoMap Map = new(480, [new TempoChange(0, 500_000), new TempoChange(1920, 1_000_000)], []);

    [TestMethod]
    public void TicksBecomeSecondsAcrossTempoChanges()
    {
        Assert.AreEqual(0, Map.SecondsAt(0), 1e-9);
        Assert.AreEqual(2, Map.SecondsAt(1920), 1e-9);
        Assert.AreEqual(3, Map.SecondsAt(2400), 1e-9);
    }

    [TestMethod]
    public void SecondsBecomeTicksAcrossTempoChanges()
    {
        Assert.AreEqual(960, Map.TickAt(1), 1e-6);
        Assert.AreEqual(2400, Map.TickAt(3), 1e-6);
        Assert.AreEqual(-480, Map.TickAt(-0.5), 1e-6);
    }

    [TestMethod]
    public void MissingChangesDefaultTo120BpmIn44()
    {
        var map = new TempoMap(192, [], []);

        Assert.AreEqual(0.5, map.SecondsAt(192), 1e-9);
        Assert.AreSequenceEqual([new TimeSignature(0, 4, 4)], [.. map.TimeSignatures]);
    }

    [TestMethod]
    public void ShiftMovesBySongTimeToTheNearestTickAndNotBeforeTheStart()
    {
        Assert.AreEqual(1920 + 240, Map.Shift(1920, TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual(1920 - 480, Map.Shift(1920, TimeSpan.FromMilliseconds(-500)));
        Assert.AreEqual(0, Map.Shift(100, TimeSpan.FromSeconds(-1)));
        Assert.AreEqual(1, Map.Shift(0, TimeSpan.FromMilliseconds(1)));
    }

    [TestMethod]
    public void BeatLinesFollowTheTimeSignature()
    {
        var map = new TempoMap(100, [], [new TimeSignature(0, 3, 4), new TimeSignature(300, 2, 8)]);

        var lines = map.BeatLines(0, 500).ToList();

        Assert.AreSequenceEqual(
            [new BeatLine(0, true), new BeatLine(100, false), new BeatLine(200, false), new BeatLine(300, true), new BeatLine(350, false), new BeatLine(400, true), new BeatLine(450, false), new BeatLine(500, true)], lines);
        Assert.AreEqual(new BeatLine(400, true), map.BeatLines(390, 420).Single());
    }

    [TestMethod]
    public void StepNotesPutsATimeBetweenNotesOnTheNextOrTheOneBefore()
    {
        Assert.AreEqual(0.5, Map.StepNotes(0.2, 1, 1), 1e-9);
        Assert.AreEqual(0, Map.StepNotes(0.2, -1, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesMovesATimeOnANoteAWholeNote()
    {
        Assert.AreEqual(1, Map.StepNotes(0.5, 1, 1), 1e-9);
        Assert.AreEqual(0, Map.StepNotes(0.5, -1, 1), 1e-9);
        Assert.AreEqual(1, Map.StepNotes(Map.SecondsAt(480), 1, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesFollowsTempoChanges()
    {
        Assert.AreEqual(2, Map.StepNotes(1.9, 1, 1), 1e-9);
        Assert.AreEqual(3, Map.StepNotes(1.9, 2, 1), 1e-9);
        Assert.AreEqual(2, Map.StepNotes(3, -1, 1), 1e-9);
        Assert.AreEqual(1.5, Map.StepNotes(2.5, -2, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesStepsByTheNoteGiven()
    {
        // Eighths, half notes, and eighth-note triplets at 120 BPM.
        Assert.AreEqual(0.25, Map.StepNotes(0.2, 1, 0.5), 1e-9);
        Assert.AreEqual(1, Map.StepNotes(0.2, 1, 2), 1e-9);
        Assert.AreEqual(0.5 / 3, Map.StepNotes(0, 1, 1.0 / 3), 1e-9);
        Assert.AreEqual(0.5, Map.StepNotes(0.5 / 3, 2, 1.0 / 3), 1e-9);

        // Sixteenths after the tempo halves.
        Assert.AreEqual(2.25, Map.StepNotes(2, 1, 0.25), 1e-9);
    }

    [TestMethod]
    public void StepNotesTakesATimeAFrameOffANoteAsOnIt()
    {
        // Thirty-seconds at 120 BPM are 62.5 ms apart. Audio seeks to whole frames, so after a step to 0.3125 s the
        // position reads back a frame short of it, or past it: the next step still moves on a whole note.
        double frame = 1.0 / 48_000;
        Assert.AreEqual(0.375, Map.StepNotes(0.3125 - frame, 1, 0.125), 1e-9);
        Assert.AreEqual(0.375, Map.StepNotes(0.3125 + frame, 1, 0.125), 1e-9);
        Assert.AreEqual(0.25, Map.StepNotes(0.3125 - frame, -1, 0.125), 1e-9);
        Assert.AreEqual(0.25, Map.StepNotes(0.3125 + frame, -1, 0.125), 1e-9);
        Assert.AreEqual(0.4375, Map.StepNotes(0.3125 - frame, 2, 0.125), 1e-9);
    }

    [TestMethod]
    public void StepNotesFromAFrameShortOfANoteKeepsMovingOn()
    {
        // As the wheel does: each step from where the last one landed, read back a frame short, on past the tempo change.
        double frame = 1.0 / 44_100;
        double at = 0;
        for (int i = 1; i <= 40; i++)
        {
            at = Map.StepNotes(at, 1, 0.125) - frame;
            Assert.AreEqual(Map.SecondsAt(i * 60), at + frame, 1e-9, $"Step {i}");
        }
    }

    [TestMethod]
    public void StepNotesRunsOnBackBeforeTheStart()
    {
        Assert.AreEqual(-0.5, Map.StepNotes(0, -1, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesOfNoneIsTheSameTime()
    {
        Assert.AreEqual(0.3, Map.StepNotes(0.3, 0, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesWithoutChangesStepsAt120Bpm()
    {
        var map = new TempoMap(192, [], []);

        Assert.AreEqual(0.5, map.StepNotes(0.3, 1, 1), 1e-9);
        Assert.AreEqual(1.5, map.StepNotes(0.5, 2, 1), 1e-9);
    }

    [TestMethod]
    public void StepNotesNeedsANoteWithALength()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Map.StepNotes(1, 1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Map.StepNotes(1, 1, double.NaN));
    }
}
