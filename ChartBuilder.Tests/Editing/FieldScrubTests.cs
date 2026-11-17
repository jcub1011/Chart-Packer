using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Properties panel's Tick, Length and Pitch fields dragged sideways: every selected note moved by the same steps from where
/// it was, rather than all set to one value, shown as it's dragged and put on the undo stack as one step.
/// </summary>
[TestClass]
public sealed class FieldScrubTests
{
    // A grid step at the default snap, 1/16, at 480 ticks a beat.
    private const int Step = 120;

    private static readonly VocalNotePlace[] TwoVocalNotes = [new(0, 60), new(480, 64)];

    private static EditorSession TwoVocalNotesSelected()
    {
        var session = Of(WithVocals([new(0, Step, 60), new(480, 240, 64)], [new(0, "la"), new(480, "di")]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, TwoVocalNotes);
        return session;
    }

    // Pitch.

    [TestMethod]
    public void ScrubbingThePitchRaisesEachNoteByTheSameSemitonesAsOneStep()
    {
        var session = TwoVocalNotesSelected();

        Assert.IsTrue(session.Tools.StartVocalPitchScrub());
        Assert.AreEqual(3, session.Tools.Scrub(3));
        Assert.AreSequenceEqual([63, 67], Vocals(session).Notes.Select(static n => n.Pitch).ToList());

        // Each step is counted from where the scrub started, not from the step before.
        Assert.AreEqual(-2, session.Tools.Scrub(-2));
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([new VocalNoteEvent(0, Step, 58), new VocalNoteEvent(480, 240, 62)], Vocals(session).Notes.ToList());
        Assert.AreEqual("la", VocalEdits.LyricAt(Vocals(session), 0), "The lyrics go with their notes.");
        Assert.AreSequenceEqual([new VocalNotePlace(0, 58), new VocalNotePlace(480, 62)], [.. session.Selection.VocalPlaces.OrderBy(static p => p.Tick)]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.AreSequenceEqual([60, 64], Vocals(session).Notes.Select(static n => n.Pitch).ToList());
    }

    [TestMethod]
    public void ThePitchStopsAsAGroupAtTheHighestAndLowestSung()
    {
        var session = Of(WithVocals([new(0, Step, 40), new(480, 240, 82)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new VocalNotePlace(0, 40), new VocalNotePlace(480, 82)]);
        session.Tools.StartVocalPitchScrub();

        Assert.AreEqual(2, session.Tools.Scrub(10));
        Assert.AreSequenceEqual([42, 84], Vocals(session).Notes.Select(static n => n.Pitch).ToList());

        Assert.AreEqual(-4, session.Tools.Scrub(-10));
        Assert.AreSequenceEqual([36, 78], Vocals(session).Notes.Select(static n => n.Pitch).ToList());
    }

    [TestMethod]
    public void ScrubbingBackToWhereItStartedChangesNothing()
    {
        var session = TwoVocalNotesSelected();
        var before = Vocals(session);

        session.Tools.StartVocalPitchScrub();
        session.Tools.Scrub(4);
        session.Tools.Scrub(0);
        session.Tools.EndScrub();

        Assert.AreSame(before, Vocals(session));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual(TwoVocalNotes, [.. session.Selection.VocalPlaces.OrderBy(static p => p.Tick)]);
    }

    [TestMethod]
    public void CancellingAScrubPutsTheNotesAndTheSelectionBack()
    {
        var session = TwoVocalNotesSelected();

        session.Tools.StartVocalPitchScrub();
        session.Tools.Scrub(5);
        session.Tools.Cancel();
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([60, 64], Vocals(session).Notes.Select(static n => n.Pitch).ToList());
        Assert.AreSequenceEqual(TwoVocalNotes, [.. session.Selection.VocalPlaces.OrderBy(static p => p.Tick)]);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsFalse(session.Tools.IsScrubbing);
    }

    [TestMethod]
    public void WithNothingSelectedThereIsNothingToScrub()
    {
        var session = Of(WithVocals([new(0, Step, 60)]));

        Assert.IsFalse(session.Tools.StartVocalPitchScrub());
        Assert.IsFalse(session.Tools.StartVocalLengthScrub());
        Assert.IsFalse(session.Tools.StartNoteLengthScrub());
        Assert.IsFalse(session.Tools.StartVocalTickScrub());
        Assert.IsFalse(session.Tools.StartNoteTickScrub());
        Assert.AreEqual(0, session.Tools.Scrub(3));
    }

    // Where the notes are.

    [TestMethod]
    public void ScrubbingAVocalTickMovesEachNoteByTheSameGridStepsWithItsLyricAsOneStep()
    {
        var session = TwoVocalNotesSelected();

        Assert.IsTrue(session.Tools.StartVocalTickScrub());
        Assert.AreEqual(2, session.Tools.Scrub(2));
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([new VocalNoteEvent(2 * Step, Step, 60), new VocalNoteEvent(480 + (2 * Step), 240, 64)], Vocals(session).Notes.ToList());
        Assert.AreEqual("la", VocalEdits.LyricAt(Vocals(session), 2 * Step), "The lyrics go with their notes.");
        Assert.AreSequenceEqual(
            [new VocalNotePlace(2 * Step, 60), new VocalNotePlace(480 + (2 * Step), 64)],
            [.. session.Selection.VocalPlaces.OrderBy(static p => p.Tick)]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.AreSequenceEqual([0L, 480L], Vocals(session).Notes.Select(static n => n.Tick).ToList());
    }

    [TestMethod]
    public void WithSnapOnTheFirstNoteScrubbedLandsOnTheGridAndTheOthersKeepTheirPlacesAroundIt()
    {
        var session = Of(WithVocals([new(130, Step, 60), new(500, Step, 62)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new VocalNotePlace(130, 60), new VocalNotePlace(500, 62)]);
        session.Tools.StartVocalTickScrub();

        session.Tools.Scrub(1);
        Assert.AreSequenceEqual([240L, 610L], Vocals(session).Notes.Select(static n => n.Tick).ToList());

        session.Tools.Scrub(-1);
        Assert.AreSequenceEqual([120L, 490L], Vocals(session).Notes.Select(static n => n.Tick).ToList());
    }

    [TestMethod]
    public void ATickScrubStopsOnceTheFirstNoteIsAtTheStart()
    {
        var session = Of(WithVocals([new(240, Step, 60), new(600, Step, 62)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new VocalNotePlace(240, 60), new VocalNotePlace(600, 62)]);
        session.Tools.StartVocalTickScrub();

        Assert.AreEqual(-2, session.Tools.Scrub(-10));
        Assert.AreSequenceEqual([0L, 360L], Vocals(session).Notes.Select(static n => n.Tick).ToList());
    }

    [TestMethod]
    public void WithSnapOffATickIsScrubbedATickAtATime()
    {
        var session = TwoVocalNotesSelected();
        session.Snap = SnapGrid.Of(16, isOn: false);

        session.Tools.StartVocalTickScrub();
        session.Tools.Scrub(7);
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([7L, 487L], Vocals(session).Notes.Select(static n => n.Tick).ToList());
    }

    [TestMethod]
    public void ScrubbingANotesTickMovesEachNoteByTheSameGridStepsAsOneStep()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote first = new(0, EditableNote.PlainLength, 1), second = new(480, 240, 2);
        track.Notes.Add(first);
        track.Notes.Add(second);
        session.Selection.Select(GuitarExpert, [first, second]);

        Assert.IsTrue(session.Tools.StartNoteTickScrub());
        session.Tools.Scrub(2);
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([first with { Tick = 2 * Step }, second with { Tick = 480 + (2 * Step) }], track.Notes.ToList());
        Assert.AreSequenceEqual([new NotePlace(2 * Step, 1), new NotePlace(480 + (2 * Step), 2)], [.. session.Selection.Places.OrderBy(static p => p.Tick)]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.AreSequenceEqual([first, second], track.Notes.ToList());
    }

    [TestMethod]
    public void CancellingANotesTickScrubPutsTheNotesAndTheSelectionBack()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote first = new(240, EditableNote.PlainLength, 1), second = new(480, 240, 2);
        track.Notes.Add(first);
        track.Notes.Add(second);
        session.Selection.Select(GuitarExpert, [first, second]);

        session.Tools.StartNoteTickScrub();
        Assert.AreEqual(-2, session.Tools.Scrub(-5));
        Assert.AreSequenceEqual([first with { Tick = 0 }, second with { Tick = 240 }], track.Notes.ToList());
        session.Tools.Cancel();

        Assert.AreSequenceEqual([first, second], track.Notes.ToList());
        Assert.AreSequenceEqual([new NotePlace(240, 1), new NotePlace(480, 2)], [.. session.Selection.Places.OrderBy(static p => p.Tick)]);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // A vocal note's length.

    [TestMethod]
    public void ScrubbingAVocalLengthAddsTheSameGridStepsToEachNoteAsOneStep()
    {
        var session = TwoVocalNotesSelected();

        Assert.IsTrue(session.Tools.StartVocalLengthScrub());
        session.Tools.Scrub(2);
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([Step * 3L, 240L + (2 * Step)], Vocals(session).Notes.Select(static n => n.Length).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AShortenedVocalNoteLastsATickAndTheScrubStopsOnceTheLongestDoes()
    {
        var session = TwoVocalNotesSelected();
        session.Tools.StartVocalLengthScrub();

        Assert.AreEqual(-1, session.Tools.Scrub(-1));
        Assert.AreSequenceEqual([1L, 240L - Step], Vocals(session).Notes.Select(static n => n.Length).ToList());

        // Two steps take the longer note down to a tick too; further changes nothing.
        Assert.AreEqual(-2, session.Tools.Scrub(-9));
        Assert.AreSequenceEqual([1L, 1L], Vocals(session).Notes.Select(static n => n.Length).ToList());
    }

    [TestMethod]
    public void WithSnapOffALengthIsScrubbedATickAtATime()
    {
        var session = TwoVocalNotesSelected();
        session.Snap = SnapGrid.Of(16, isOn: false);

        session.Tools.StartVocalLengthScrub();
        session.Tools.Scrub(7);
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([Step + 7L, 247L], Vocals(session).Notes.Select(static n => n.Length).ToList());
    }

    // A note's sustain.

    [TestMethod]
    public void ScrubbingANotesLengthAddsTheSameGridStepsToEachSustainAsOneStep()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote plain = new(0, EditableNote.PlainLength, 1), held = new(480, 240, 2);
        track.Notes.Add(plain);
        track.Notes.Add(held);
        session.Selection.Select(GuitarExpert, [plain, held]);

        Assert.IsTrue(session.Tools.StartNoteLengthScrub());
        session.Tools.Scrub(2);
        session.Tools.EndScrub();

        // A note without a sustain grows one from its tick, as the right button's drag gives it.
        Assert.AreSequenceEqual([new EditableNote(0, 2 * Step, 1), new EditableNote(480, 240 + (2 * Step), 2)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSequenceEqual([plain, held], track.Notes.ToList());
    }

    [TestMethod]
    public void AShortenedSustainLeavesAPlainNoteAndTheScrubStopsOnceTheLongestDoes()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote longer = new(0, 480, 1), shorter = new(480, Step, 2);
        track.Notes.Add(longer);
        track.Notes.Add(shorter);
        session.Selection.Select(GuitarExpert, [longer, shorter]);
        session.Tools.StartNoteLengthScrub();

        Assert.AreEqual(-2, session.Tools.Scrub(-2));
        Assert.AreSequenceEqual([new EditableNote(0, 240, 1), new EditableNote(480, EditableNote.PlainLength, 2)], track.Notes.ToList());

        Assert.AreEqual(-4, session.Tools.Scrub(-10));
        Assert.IsTrue(track.Notes.All(static n => n.Length == EditableNote.PlainLength));
    }

    [TestMethod]
    public void ScrubbingADrumsLengthChangesNothingAndSaysWhy()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        EditableNote pad = new(0, EditableNote.PlainLength, 1);
        track.Notes.Add(pad);
        session.Selection.Select(DrumsExpert, [pad]);

        session.Tools.StartNoteLengthScrub();
        Assert.AreEqual(0, session.Tools.Scrub(3));
        session.Tools.EndScrub();

        Assert.AreSequenceEqual([pad], track.Notes.ToList());
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }
}
