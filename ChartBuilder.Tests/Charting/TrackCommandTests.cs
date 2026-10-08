using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// The commands that change a track as a whole or in one go: <see cref="ReplaceNotes"/>, <see cref="ReplaceTrack"/>,
/// and Copy difficulty (charter-spec C9 item 1); and how notes are merged in (<see cref="NoteMerge"/>).
/// </summary>
[TestClass]
public sealed class TrackCommandTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;

    // ReplaceNotes.

    [TestMethod]
    public void ReplaceNotesTakesOutAndPutsInAndClearsAnEmptiedChordsMarking()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0), Note(480, 1), Note(960, 2));
        Mark(chart, Guitar, Expert, 480, ChordFlags.ForceHopo);

        var command = new ReplaceNotes(Guitar, Expert, [Note(480, 1)], [Note(240, 3), Note(960, 4)], [new FlaggedChord(960, ChordFlags.Tap)]);
        var after = AppliesRevertsAndRedoes(chart, command);

        var track = after.PartOf(Guitar)![Expert];
        Assert.AreSequenceEqual([Note(0, 0), Note(240, 3), Note(960, 2), Note(960, 4)], track.Notes.ToList());
        Assert.AreEqual(ChordFlags.None, track.FlagsAt(480));
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(960));
    }

    [TestMethod]
    public void ReplaceNotesPutsANoteWhereOneTakenOutWas()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0));

        var after = AppliesRevertsAndRedoes(chart, new ReplaceNotes(Guitar, Expert, [Note(0, 0)], [Note(0, 0) with { Length = 240 }]));

        Assert.AreEqual(240, after.PartOf(Guitar)![Expert].Notes.Single().Length);
    }

    [TestMethod]
    public void ReplaceNotesRefusesANoteOnOneThatStays()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0), Note(480, 1));

        Refuses<InvalidOperationException>(chart, new ReplaceNotes(Guitar, Expert, [Note(0, 0)], [Note(480, 1) with { Length = 60 }]));
    }

    [TestMethod]
    public void ReplaceNotesRefusesANoteTheTrackHasNot()
    {
        var chart = NewChart(Guitar);

        Refuses<InvalidOperationException>(chart, new ReplaceNotes(Guitar, Expert, [Note(0, 0)], []));
    }

    [TestMethod]
    public void ReplaceNotesRefusesMarkingAChordItLeavesEmpty()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0));

        Refuses<InvalidOperationException>(chart, new ReplaceNotes(Guitar, Expert, [Note(0, 0)], [], [new FlaggedChord(0, ChordFlags.Tap)]));
    }

    [TestMethod]
    public void ReplaceNotesRefusesForcingADrumChord()
    {
        var chart = NewChart(Drums);

        Refuses<InvalidOperationException>(chart, new ReplaceNotes(Drums, Expert, [], [Note(0, 1)], [new FlaggedChord(0, ChordFlags.ForceHopo)]));
    }

    // ReplaceTrack.

    [TestMethod]
    public void ReplaceTrackPutsInNewContentsAndUndoesExactly()
    {
        var chart = NewChart(Drums);
        Add(chart, Drums, Hard, Note(0, 1), Note(480, 2));
        new AddDiscoFlip(Hard, 0, 960).Apply(chart);

        var command = new ReplaceTrack(Drums, Hard, [Note(240, 3)], [], [new DrumMix(240, 0, "d")]);
        var after = AppliesRevertsAndRedoes(chart, command);

        var track = after.PartOf(Drums)![Hard];
        Assert.AreSequenceEqual([Note(240, 3)], track.Notes.ToList());
        Assert.AreSequenceEqual([new DrumMix(240, 0, "d")], track.Mixes.ToList());
    }

    [TestMethod]
    public void ReplaceTrackLeavesTheMixesWithoutNewOnes()
    {
        var chart = NewChart(Drums);
        new AddDiscoFlip(Hard, 0, 960).Apply(chart);
        var mixes = chart.PartOf(Drums)![Hard].Mixes.ToList();

        var after = AppliesRevertsAndRedoes(chart, new ReplaceTrack(Drums, Hard, [Note(240, 3)], []));

        Assert.AreSequenceEqual(mixes, after.PartOf(Drums)![Hard].Mixes.ToList());
    }

    [TestMethod]
    public void ReplaceTrackRefusesTwoNotesAtOnePlace()
    {
        var chart = NewChart(Guitar);

        Refuses<ArgumentException>(chart, new ReplaceTrack(Guitar, Expert, [Note(0, 1), Note(0, 1) with { Length = 60 }], []));
    }

    // Copy difficulty (charter-spec C9 item 1).

    [TestMethod]
    public void CopyDifficultyCopiesTheNotesAndTheirMarkingInPlaceOfTheTargets()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0), Note(480, 1) with { Length = 240 }, Note(480, 3));
        Mark(chart, Guitar, Expert, 480, ChordFlags.ForceHopo);
        Add(chart, Guitar, Hard, Note(960, 4));
        Mark(chart, Guitar, Hard, 960, ChordFlags.Tap);

        var after = AppliesRevertsAndRedoes(chart, new CopyDifficulty(Guitar, Expert, Hard));

        var hard = after.PartOf(Guitar)![Hard];
        Assert.AreSequenceEqual(after.PartOf(Guitar)![Expert].Notes.ToList(), hard.Notes.ToList());
        Assert.AreSequenceEqual([new FlaggedChord(480, ChordFlags.ForceHopo)], hard.FlaggedChords.ToList());
    }

    [TestMethod]
    public void CopyDifficultyCopiesDiscoFlipAndLeavesOutTheTwoTimesKick()
    {
        var chart = NewChart(Drums);
        Add(chart, Drums, Expert, Note(0, 0), new EditableNote(0, 1, 0, DoubleKick: true), Note(240, 1));
        new AddDiscoFlip(Expert, 0, 480).Apply(chart);

        var command = new CopyDifficulty(Drums, Expert, Hard);
        var after = AppliesRevertsAndRedoes(chart, command);

        var hard = after.PartOf(Drums)![Hard];
        Assert.AreSequenceEqual([Note(0, 0), Note(240, 1)], hard.Notes.ToList());
        Assert.AreSequenceEqual(after.PartOf(Drums)![Expert].Mixes.ToList(), hard.Mixes.ToList());
        Assert.AreEqual(1, command.DroppedDoubleKicks);
    }

    [TestMethod]
    public void CopyDifficultyIsUndoneInOneStep()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0));
        Add(chart, Guitar, Hard, Note(480, 2));
        var start = chart.Clone();
        var stack = new ChartUndoStack(chart);

        stack.Execute(new CopyDifficulty(Guitar, Expert, Hard));
        stack.Undo();

        AssertSame(start, chart, "after undoing the copy");
    }

    [TestMethod]
    public void CopyDifficultyRefusesADifficultyToItself() =>
        Assert.Throws<ArgumentException>(() => new CopyDifficulty(Guitar, Expert, Expert));

    // NoteMerge.

    [TestMethod]
    public void OverwritingTakesOutEverythingInTheStretch()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0), Note(480, 1), Note(960, 2), Note(1440, 3));
        var track = chart.PartOf(Guitar)![Expert];

        var plan = NoteMerge.Plan(track, [Note(720, 4)], [], (480, 960));

        Assert.AreSequenceEqual([Note(480, 1), Note(960, 2)], plan.Remove.ToList());
        Assert.AreSequenceEqual([Note(720, 4)], plan.Add.ToList());
    }

    [TestMethod]
    public void MergingFretsReplacesTheChordAtEachTickAndKeepsTheRest()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0), Note(0, 2), Note(480, 1));
        Mark(chart, Guitar, Expert, 0, ChordFlags.ForceStrum);
        var track = chart.PartOf(Guitar)![Expert];

        var plan = NoteMerge.Plan(track, [new EditableNote(0, 1, ChartNote.OpenLane)], [], clear: null);

        Assert.AreSequenceEqual([Note(0, 0), Note(0, 2)], plan.Remove.ToList());
        Assert.AreSequenceEqual([new FlaggedChord(0, ChordFlags.None)], plan.Flags.ToList());
        Assert.IsNull(EditorTools.PlacementRefusal(track, plan.Add, plan.Remove as IReadOnlyCollection<EditableNote>));
    }

    [TestMethod]
    public void MergingDrumsKeepsTheOtherPadsOnATick()
    {
        var chart = NewChart(Drums);
        Add(chart, Drums, Expert, Note(0, 1), Note(0, 3));
        var track = chart.PartOf(Drums)![Expert];

        var plan = NoteMerge.Plan(track, [Note(0, 0), Note(0, 3) with { Dynamics = NoteDynamics.Accent }], [], clear: null);

        Assert.AreSequenceEqual([Note(0, 3)], plan.Remove.ToList());
        Assert.AreSequenceEqual([Note(0, 0), Note(0, 3) with { Dynamics = NoteDynamics.Accent }], plan.Add.ToList());
    }

    [TestMethod]
    public void ANotePutBackAsItWasChangesNothing()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, 0));

        var plan = NoteMerge.Plan(chart.PartOf(Guitar)![Expert], [Note(0, 0)], [], (0, 100));

        Assert.IsTrue(plan.IsEmpty);
    }

    internal static EditableNote Note(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    internal static void Add(EditableChart chart, ChartInstrument part, ChartDifficulty difficulty, params EditableNote[] notes) =>
        new AddNotes(part, difficulty, notes).Apply(chart);

    internal static void Mark(EditableChart chart, ChartInstrument part, ChartDifficulty difficulty, long tick, ChordFlags flags) =>
        new SetChordFlags(part, difficulty, [tick], flags, on: true).Apply(chart);
}
