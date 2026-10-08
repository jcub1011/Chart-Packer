using ChartBuilder.Tests.Editing;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;

namespace ChartBuilder.Tests.Charting;

/// <summary><see cref="MoveNotes"/> with a number of ticks for each note, as Nudge to onset moves them (charter-spec C14 item 3).</summary>
[TestClass]
public sealed class MoveNotesEachTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    [TestMethod]
    public void EachNoteMovesByItsOwnTicks()
    {
        var chart = Chart([Note(480, 1), Note(960, 2), Note(1440, 3)]);
        var notes = chart.PartOf(Guitar)![Expert].Notes.ToList();

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [(notes[0], 5), (notes[1], -10), (notes[2], 0)]));

        CollectionAssert.AreEqual(new long[] { 485, 950, 1440 }, after.PartOf(Guitar)![Expert].Notes.Select(n => n.Tick).ToArray());
    }

    [TestMethod]
    public void AChordsMarkingMovesOnlyWhenItsNotesMoveTogether()
    {
        var chart = Chart([Note(480, 1), Note(480, 2), Note(960, 1), Note(960, 2)]);
        new SetChordFlags(Guitar, Expert, [480, 960], ChordFlags.Tap, on: true).Apply(chart);
        var notes = chart.PartOf(Guitar)![Expert].Notes.ToList();

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [(notes[0], 6), (notes[1], 6), (notes[2], 4), (notes[3], -4)]));

        var track = after.PartOf(Guitar)![Expert];
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(486), "Moved together, the chord takes its tap with it.");
        Assert.AreEqual(ChordFlags.None, track.FlagsAt(480));
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(960), "Moved apart, the tap stays with the tick.");
    }

    [TestMethod]
    public void ANoteCantMoveOntoOneThatIsntMovingOrBeforeTheStart()
    {
        var chart = Chart([Note(0, 1), Note(480, 1), Note(500, 1)]);
        var notes = chart.PartOf(Guitar)![Expert].Notes.ToList();

        Refuses<InvalidOperationException>(chart, new MoveNotes(Guitar, Expert, [(notes[1], 20)]));
        Refuses<InvalidOperationException>(chart, new MoveNotes(Guitar, Expert, [(notes[0], -1)]));
    }

    private static EditableChart Chart(EditableNote[] notes)
    {
        var chart = EditorSessions.Guitar();
        new AddNotes(Guitar, Expert, notes).Apply(chart);
        return chart;
    }

    private static EditableNote Note(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);
}
