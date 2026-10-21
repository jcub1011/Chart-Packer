using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Quantize from the note menu: the selected notes, chord by chord, and their sustains' ends put on a grid, as one step. At
/// 480 ticks a beat, a 1/16 step is 120 ticks.
/// </summary>
[TestClass]
public sealed class NoteQuantizeTests
{
    private static readonly SnapGrid Sixteenths = SnapGrid.Of(16);

    [TestMethod]
    public void AChordGoesToTheNearestStepAndASustainsEndToo()
    {
        var session = With(Guitar(), GuitarExpert, new(130, 470, 0), new(250, 1, 1));

        var plan = Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.AreEqual(2, plan.Moved);
        Assert.AreSequenceEqual([(120L, 0, 480L), (240L, 1, EditableNote.PlainLength)], Notes(Expert(session)));
    }

    [TestMethod]
    public void ASustainEndingWhereItStartsIsAPlainNote()
    {
        var session = With(Guitar(), GuitarExpert, new EditableNote(118, 20, 0));

        Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.AreSequenceEqual([(120L, 0, EditableNote.PlainLength)], Notes(Expert(session)));
    }

    [TestMethod]
    public void NotesPlayedALittleApartBecomeOneChordAndTwoAtOnePlaceKeepTheLonger()
    {
        var session = With(Guitar(), GuitarExpert, new(470, 1, 0), new(484, 250, 0), new(490, 1, 1));

        var plan = Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.AreSequenceEqual([(480L, 0, 240L), (480L, 1, EditableNote.PlainLength)], Notes(Expert(session)));
        Assert.AreSequenceEqual([(480L, 0), (480L, 1)], session.Selection.Notes.Select(n => (n.Tick, n.Lane)).Order().ToList());
        Assert.IsEmpty(plan.Blocked);
    }

    [TestMethod]
    public void AChordThatWouldLandOnANoteNotSelectedStays()
    {
        var session = With(Guitar(), GuitarExpert, new(470, 1, 0), new(480, 1, 0), new(970, 1, 2));

        var plan = Quantize(session, GuitarExpert, Sixteenths, new(470, 1, 0), new(970, 1, 2));

        Assert.AreSequenceEqual([470L], plan.Blocked);
        Assert.AreEqual(1, plan.Moved);
        Assert.AreSequenceEqual([(470L, 0), (480L, 0), (960L, 2)], Notes(Expert(session)).Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void AnOpenNoteLandingWithFretsIsLeftOut()
    {
        var session = With(Guitar(), GuitarExpert, new(475, 1, ChartNote.OpenLane), new(485, 1, 1));

        Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.AreSequenceEqual([(480L, 1)], Notes(Expert(session)).Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void AWholeChordsMarkingGoesWithIt()
    {
        var session = With(Guitar(), GuitarExpert, new(130, 1, 0), new(130, 1, 1));
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [130], ChordFlags.ForceHopo, on: true).Apply(session.Chart);

        Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.AreEqual(ChordFlags.ForceHopo, Expert(session).FlagsAt(120));
        Assert.AreEqual(ChordFlags.None, Expert(session).FlagsAt(130));
    }

    [TestMethod]
    public void DrumsHitsALittleApartBecomeOneChord()
    {
        var session = With(Drums(), DrumsExpert, new(478, 1, 1), new(482, 1, 2), new(490, 1, 1));
        var track = session.TrackOf(DrumsExpert)!;

        Quantize(session, DrumsExpert, Sixteenths, [.. track.Notes]);

        Assert.AreSequenceEqual([(480L, 1), (480L, 2)], Notes(track).Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void NotesOnTheGridAlreadyChangeNothing()
    {
        var session = With(Guitar(), GuitarExpert, new(480, 1, 0), new(960, 480, 1));

        var plan = Quantize(session, GuitarExpert, Sixteenths, [.. Expert(session).Notes]);

        Assert.IsNull(plan.Command);
        Assert.AreEqual(0, plan.Moved);
        Assert.IsFalse(session.UndoStack.CanUndo);
    }

    [TestMethod]
    public void AnUndoPutsThemBack()
    {
        var session = With(Guitar(), GuitarExpert, new(130, 470, 0), new(470, 1, 1), new(490, 1, 2));
        var start = session.Chart.Clone();

        Quantize(session, GuitarExpert, SnapGrid.Of(8), [.. Expert(session).Notes]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();

        AssertSame(start, session.Chart, "after one undo");
    }

    private static EditorSession With(EditableChart chart, ChartTrackKey key, params EditableNote[] notes)
    {
        var session = Of(chart, focused: key);
        new AddNotes(key.Instrument, key.Difficulty, notes).Apply(session.Chart);
        return session;
    }

    private static QuantizePlan Quantize(EditorSession session, ChartTrackKey key, SnapGrid grid, params EditableNote[] selected)
    {
        session.Selection.Select(key, selected);
        return session.Tools.QuantizeSelection(grid)!;
    }

    private static List<(long Tick, int Lane, long Length)> Notes(EditableTrack track) =>
        [.. track.Notes.Select(static n => (n.Tick, n.Lane, n.Length))];
}
