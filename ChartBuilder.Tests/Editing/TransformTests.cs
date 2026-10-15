using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>The transforms on the selection (charter-spec C15 item 4): lefty flip, shifting lanes, sustains and stretching.</summary>
[TestClass]
public sealed class TransformTests
{
    [TestMethod]
    public void LeftyFlipMirrorsFiveFretAndKeepsOpensAndForcing()
    {
        var session = Notes(Guitar(), GuitarExpert, new(0, 1, 0), new(0, 1, 3), new(480, 1, 2), new(960, 1, ChartNote.OpenLane));
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [0], ChordFlags.ForceHopo, on: true).Apply(session.Chart);
        SelectAll(session, GuitarExpert);

        session.Tools.LeftyFlip();

        Assert.AreSequenceEqual([(0L, 1), (0L, 4), (480L, 2), (960L, ChartNote.OpenLane)], Lanes(session, GuitarExpert));
        Assert.AreEqual(ChordFlags.ForceHopo, Expert(session).FlagsAt(0));
    }

    [TestMethod]
    public void LeftyFlipMirrorsGhlColumnsKeepingColours()
    {
        var session = Notes(Ghl(), GhlExpert, new(0, 1, 0), new(480, 1, 5), new(960, 1, 1));
        SelectAll(session, GhlExpert);

        session.Tools.LeftyFlip();

        Assert.AreSequenceEqual([(0L, 2), (480L, 3), (960L, 1)], Lanes(session, GhlExpert));
    }

    [TestMethod]
    public void LeftyFlipRefusesDrums()
    {
        var session = Notes(Drums(), DrumsExpert, new EditableNote(0, 1, 1));
        SelectAll(session, DrumsExpert);

        session.Tools.LeftyFlip();

        Assert.IsFalse(session.UndoStack.CanUndo);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void ShiftingLanesStopsAtTheEdgeAndJoinsNotesThatMeet()
    {
        var session = Notes(Guitar(), GuitarExpert, new(0, 1, 3), new(0, 1, 4), new(480, 1, 1), new(960, 1, ChartNote.OpenLane));
        SelectAll(session, GuitarExpert);

        session.Tools.ShiftLanes(1);

        Assert.AreSequenceEqual([(0L, 4), (480L, 2), (960L, ChartNote.OpenLane)], Lanes(session, GuitarExpert));
        session.Undo();
        Assert.AreEqual(4, Expert(session).Notes.Count);
    }

    [TestMethod]
    public void ShiftingDrumsLeavesTheKick()
    {
        var session = Notes(Drums(DrumsVariant.FourLane), DrumsExpert, new(0, 1, 0), new(0, 1, 4), new(480, 1, 1));
        SelectAll(session, DrumsExpert);

        session.Tools.ShiftLanes(1);

        Assert.AreSequenceEqual([(0L, 0), (0L, 4), (480L, 2)], Lanes(session, DrumsExpert));
    }

    [TestMethod]
    public void SustainsUntilTheNextNoteRunToItsTick()
    {
        var session = Notes(Guitar(), GuitarExpert, new(0, 1, 0), new(0, 1, 2), new(480, 1, 1), new(1440, 1, 3));
        SelectAll(session, GuitarExpert);

        session.Tools.SetSustains(SustainChange.UntilNextNote);

        Assert.AreSequenceEqual([480L, 480L, 960L, 1L], Expert(session).Notes.Select(n => n.Length).ToList());
        session.Tools.SetSustains(SustainChange.Clear);
        Assert.IsTrue(Expert(session).Notes.All(n => n.Length == EditableNote.PlainLength));
    }

    [TestMethod]
    public void ATransformOverARangeChangesEveryTrackInOneStep()
    {
        var session = Notes(Guitar(), GuitarExpert, new(0, 1, 0), new(480, 1, 1));
        var hard = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Hard);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Hard, [new(0, 1, 1)]).Apply(session.Chart);
        var start = session.Chart.Clone();
        session.Selection.SelectRange(0, 480, [GuitarExpert, hard]);

        session.Tools.LeftyFlip();

        Assert.AreSequenceEqual([(0L, 4), (480L, 3)], Lanes(session, GuitarExpert));
        Assert.AreSequenceEqual([(0L, 3)], Lanes(session, hard));
        session.Undo();
        AssertSame(start, session.Chart, "after one undo");
    }

    // Stretching the timing.

    [TestMethod]
    public void HalvingASpanKeepsEveryNotesTimeAndMovesWhatsAfter()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 1), new(960, 240, 2), new(1440, 1, 3), new(3840, 1, 4)]).Apply(chart);
        var before = chart.Clone();
        var map = before.TempoMap.ToTempoMap();
        var session = Of(chart);
        session.Selection.SetRange(480, 1920);

        session.Tools.StretchSelection(0.5);

        // The span of 1440 ticks is now 720, its tempo twice as slow; what's after it moves 720 ticks earlier.
        var after = chart.TempoMap.ToTempoMap();
        Assert.AreSequenceEqual([0L, 480L, 720L, 960L, 3120L], Expert(session).Notes.Select(n => n.Tick).ToList());
        foreach (var (was, now) in before.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.Zip(Expert(session).Notes))
        {
            Assert.AreEqual(map.SecondsAt(was.Tick), after.SecondsAt(now.Tick), 0.001);
            if (was.Length > EditableNote.PlainLength)
                Assert.AreEqual(map.SecondsAt(was.Tick + was.Length), after.SecondsAt(now.Tick + now.Length), 0.001);
        }

        Assert.AreSequenceEqual([(0L, 500_000L), (480L, 1_000_000L), (1200L, 500_000L)], chart.TempoMap.Tempos.Select(t => (t.Tick, t.MicrosecondsPerQuarter)).ToList());
        session.Undo();
        AssertSame(before, chart, "after one undo");
    }

    [TestMethod]
    public void StretchingRetimesThePassthroughTracks()
    {
        var chart = Guitar();
        // A note-on at tick 960 and its note-off at 1000, on a track Chart Builder doesn't edit.
        byte[] chunk = [.. "MTrk"u8, 0, 0, 0, 12, 0x87, 0x40, 0x90, 60, 100, 40, 0x80, 60, 0, 0, 0xFF, 0x2F];
        chunk = [.. chunk, 0];
        chunk[7] = (byte)(chunk.Length - 8);
        chart.AddPassthrough(new PassthroughTrack(chunk, "VENUE"));
        var command = TempoMapEdits.Stretch(chart, 480, 1440, 960).Command!;

        AppliesRevertsAndRedoes(chart, command);

        var moved = chart.Passthrough[0].Chunk.ToArray();
        var events = MidiFile.ReadEvents(moved, new MidiChunk("MTrk", 0, PassthroughTrack.HeaderLength, moved.Length - PassthroughTrack.HeaderLength));
        Assert.AreSequenceEqual([720L, 740L], events.Take(2).Select(e => e.Tick).ToList());
    }

    [TestMethod]
    public void StretchingKeepsAnAnchorsTimeAndMovesItWithItsMarker()
    {
        var chart = Guitar();
        new AddTempo(new TempoChange(1920, 500_000)).Apply(chart);
        new SetAnchor(1920, 2_000_000).Apply(chart);

        TempoMapEdits.Stretch(chart, 0, 960, 1920).Command!.Apply(chart);

        Assert.AreSequenceEqual([new TempoAnchor(2880, 2_000_000)], chart.TempoMap.Anchors.ToList());
    }

    [TestMethod]
    public void StretchingToNothingOrToTheSameEndIsRefused()
    {
        var chart = Guitar();

        Assert.IsTrue(TempoMapEdits.Stretch(chart, 480, 960, 480).IsRefused);
        Assert.IsTrue(TempoMapEdits.Stretch(chart, 480, 960, 960).IsRefused);
        Assert.IsTrue(TempoMapEdits.Stretch(chart, 480, 480, 960).IsRefused);
    }

    [TestMethod]
    public void TheStretchMapNeverDecreases()
    {
        var map = TickMaps.Stretch(480, 1440, 600);
        long last = -1;
        for (long tick = 0; tick < 3000; tick += 7)
        {
            Assert.IsGreaterThanOrEqualTo(last, map(tick));
            last = map(tick);
        }

        Assert.AreEqual(480, map(480));
        Assert.AreEqual(600, map(1440));
        Assert.AreEqual(1600, map(2440));
    }

    private static EditorSession Notes(EditableChart chart, ChartTrackKey key, params EditableNote[] notes)
    {
        new AddNotes(key.Instrument, key.Difficulty, notes).Apply(chart);
        return Of(chart, focused: key);
    }

    private static void SelectAll(EditorSession session, ChartTrackKey key) => session.Selection.Select(key, session.TrackOf(key)!.Notes);

    private static List<(long, int)> Lanes(EditorSession session, ChartTrackKey key) => [.. session.TrackOf(key)!.Notes.Select(n => (n.Tick, n.Lane))];
}
