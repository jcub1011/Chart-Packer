using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Alignment tool on an instrument (charter-spec C3 item 7): every difficulty with notes shifted and stretched in the
/// audio's time with the phrases and tom markers they share, or only the difficulty in focus, each change one undoable
/// step. At 480 ticks a beat and 120 BPM a second is 960 ticks, and a 1/16 step 120.
/// </summary>
[TestClass]
public sealed class InstrumentAlignmentTests
{
    private static readonly ChartTrackKey GuitarHard = new(ChartInstrument.Guitar, ChartDifficulty.Hard);

    // Expert and Hard: notes at 1 s and 2 s, half a second each, the second forced HOPO on Expert; star power over the
    // first and a solo over both.
    private static EditableChart Song()
    {
        var chart = Guitar();
        foreach (var difficulty in new[] { ChartDifficulty.Expert, ChartDifficulty.Hard })
            new AddNotes(ChartInstrument.Guitar, difficulty, [new EditableNote(960, 480, 0), new EditableNote(1920, 480, 1)]).Apply(chart);
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [1920], ChordFlags.ForceHopo, on: true).Apply(chart);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 960, 480)).Apply(chart);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.Solo, 960, 1440)).Apply(chart);
        return chart;
    }

    private static EditorSession Aligning(EditableChart chart, ChartTrackKey? focused = null)
    {
        var session = Of(chart, focused: focused ?? GuitarExpert);
        session.Tool = EditorTool.Align;
        return session;
    }

    private static EditorSession AligningWithSnapOff(EditableChart chart)
    {
        var session = Aligning(chart);
        session.Snap = SnapGrid.Of(16, isOn: false);
        return session;
    }

    private static EditableTrack Track(EditorSession session, ChartTrackKey key) => session.Chart.PartOf(key.Instrument)![key.Difficulty];

    private static long[] Ticks(EditorSession session, ChartTrackKey? key = null) => [.. Track(session, key ?? GuitarExpert).Notes.Select(static n => n.Tick)];

    private static Phrase[] Phrases(EditorSession session, ChartInstrument part = ChartInstrument.Guitar) => [.. session.Chart.PartOf(part)!.Phrases];

    [TestMethod]
    public void AShiftMovesEveryDifficultysNotesChordFlagsAndSharedPhrasesInOneStep()
    {
        var session = Aligning(Song());

        Assert.IsTrue(session.Alignment.Apply(0.5, 1));

        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session, GuitarHard));
        Assert.AreEqual(new FlaggedChord(2400, ChordFlags.ForceHopo), Track(session, GuitarExpert).FlaggedChords.Single());
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 1440, 480), new Phrase(PhraseKind.Solo, 1440, 1440)], Phrases(session));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreEqual(0.5, session.Alignment.Offset);

        session.Undo();
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session, GuitarHard));
        Assert.AreEqual(1920L, Track(session, GuitarExpert).FlaggedChords.Single().Tick);
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 960, 480), new Phrase(PhraseKind.Solo, 960, 1440)], Phrases(session));
    }

    [TestMethod]
    public void WithoutAllDifficultiesOnlyTheOneInFocusMovesAndTheSharedPhrasesStay()
    {
        var session = Aligning(Song());
        session.Alignment.AllDifficulties = false;

        session.Alignment.Apply(0.5, 1);

        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session, GuitarHard));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 960, 480), new Phrase(PhraseKind.Solo, 960, 1440)], Phrases(session));
        Assert.IsTrue(session.Alignment.Aligns(GuitarExpert));
        Assert.IsFalse(session.Alignment.Aligns(GuitarHard));
    }

    [TestMethod]
    public void ASpeedPlaysTheNotesFasterFromTheFirstNote()
    {
        var session = Aligning(Song());

        session.Alignment.Apply(0, 2);

        // The second note, a second after the first, is half a second after it; each note is half as long.
        Assert.AreSequenceEqual([960L, 1440L], Ticks(session));
        Assert.IsTrue(Track(session, GuitarExpert).Notes.All(static n => n.Length == 240));
        Assert.AreEqual(new Phrase(PhraseKind.Solo, 960, 720), Phrases(session)[1]);
        Assert.AreEqual((1.0, 1.75), session.Alignment.Span);
    }

    [TestMethod]
    public void TheShiftIsInTheAudiosTimeNotItsTicks()
    {
        // 240 BPM from 1 s: a tick is half as long after it as before it.
        var chart = Song();
        TempoMapEdits.AddMarker(chart, 960).Command!.Apply(chart);
        new SetTempo(new TempoChange(960, 500_000), 250_000).Apply(chart);
        var session = Aligning(chart);

        session.Alignment.Apply(-0.25, 1);

        // 1 s and 1.5 s go to 0.75 s, before the change, and 1.25 s, after it: a shift of ticks would put the second at 1680.
        Assert.AreSequenceEqual([720L, 1440L], Ticks(session));
    }

    [TestMethod]
    public void NotesThatLandOnOneAnotherAreKeptOnceAndTheStatusSaysSo()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(960, 0, 1), new EditableNote(961, 0, 0), new EditableNote(962, 0, 0)]).Apply(chart);
        var session = Aligning(chart);

        // At 150%, a tick after the first note goes to 960.67 and two after it to 961.33: both to 961.
        Assert.IsTrue(session.Alignment.Apply(0, 1.5));

        Assert.AreSequenceEqual([960L, 961L], Ticks(session));
        Assert.IsNotNull(session.Status);
        Assert.Contains("kept once", session.Status);
    }

    [TestMethod]
    public void OnDrumsTomMarkersFillsAndDiscoFlipsMoveToo()
    {
        var chart = Drums();
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(960, 0, 2), new EditableNote(1920, 0, 3)]).Apply(chart);
        new SetToms([(2, 960)], tom: true).Apply(chart);
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 1440, 480)).Apply(chart);
        new AddDiscoFlip(ChartDifficulty.Expert, 960, 1920).Apply(chart);
        long[] mixes = [.. chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Mixes.Select(static m => m.Tick)];
        long[] toms = [.. chart.PartOf(ChartInstrument.Drums)!.TomMarkers.Select(static m => m.Tick)];
        var session = Aligning(chart, DrumsExpert);

        session.Alignment.Apply(0.5, 1);

        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session, DrumsExpert));
        Assert.AreSequenceEqual([.. toms.Select(static t => t + 480)], [.. drums.TomMarkers.Select(static m => m.Tick)]);
        Assert.AreEqual(new Phrase(PhraseKind.DrumFill, 1920, 480), drums.Phrases.Single());
        Assert.AreSequenceEqual([.. mixes.Select(static t => t + 480)], [.. drums[ChartDifficulty.Expert].Mixes.Select(static m => m.Tick)]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AFillMovedOntoCodaBecomesABigRockEnding()
    {
        var chart = Drums();
        new AddTextEvent(null, TextEvent.Of(2400, "[coda]")).Apply(chart);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(960, 0, 2), new EditableNote(1920, 0, 3)]).Apply(chart);
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 1920, 240)).Apply(chart);
        var session = Aligning(chart, DrumsExpert);

        Assert.IsTrue(session.Alignment.Apply(0.5, 1));

        Assert.AreEqual(new Phrase(PhraseKind.BigRockEnding, 2400, 240), session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.Single());
    }

    [TestMethod]
    public void UndoAndRedoBringBackEachStepsValues()
    {
        var session = Aligning(Song());
        session.Alignment.Apply(0.5, 1);
        session.Alignment.Apply(1, 1.25);

        session.Undo();
        Assert.AreEqual((0.5, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        session.Undo();
        Assert.AreEqual((0.0, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        session.Redo();
        Assert.AreEqual((0.5, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
    }

    [TestMethod]
    public void AnEditMadeAnotherWayIsTakenAsTheStartAgain()
    {
        var session = Aligning(Song());
        session.Alignment.Apply(0.5, 1);

        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(3840, 240, 2)]));

        Assert.AreEqual((0.0, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        Assert.AreEqual((1.5, 4.25), session.Alignment.Span);
    }

    [TestMethod]
    public void WithSnapOffDraggingTheNotesInTheLaneShiftsThemFreely()
    {
        var session = AligningWithSnapOff(Song());
        Assert.AreEqual(AlignGrip.Body, session.Tools.AlignGripAt(At(session, 1500, 0)));

        DragFromTo(session, At(session, 1500, 0), At(session, 1977, 0));

        Assert.AreSequenceEqual([1437L, 2397L], Ticks(session));
        Assert.AreSequenceEqual([1437L, 2397L], Ticks(session, GuitarHard));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void WithSnapOnDraggingTheNotesPutsTheFirstOnTheGrid()
    {
        var session = Aligning(Song());

        // The first note dragged to 1437 goes to the step at 1440.
        DragFromTo(session, At(session, 1500, 0), At(session, 1977, 0));

        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreEqual(0.5, session.Alignment.Offset, 1e-9);
    }

    [TestMethod]
    public void TheEndsHandleStretchesTheNotesWithTheFirstKept()
    {
        var session = AligningWithSnapOff(Song());
        Assert.AreEqual(AlignGrip.End, session.Tools.AlignGripAt(At(session, 2410, null)));

        // The last note's end, at 2.5 s, dragged to 3.5 s: the first note stays at 1 s, the rest take 2.5 s, not 1.5 s.
        DragFromTo(session, At(session, 2410, null), At(session, 3370, null));

        Assert.AreEqual(0.6, session.Alignment.Speed, 1e-9);
        Assert.AreSequenceEqual([960L, 2560L], Ticks(session));
        Assert.AreEqual(3360L, Track(session, GuitarExpert).Notes[^1].Tick + Track(session, GuitarExpert).Notes[^1].Length);
    }

    [TestMethod]
    public void TheStartsHandleStretchesTheNotesWithTheLastEndKept()
    {
        var session = AligningWithSnapOff(Song());
        Assert.AreEqual(AlignGrip.Start, session.Tools.AlignGripAt(At(session, 950, null)));

        // The first note, at 1 s, dragged to 1.5 s: the last note's end stays at 2.5 s.
        DragFromTo(session, At(session, 950, null), At(session, 1430, null));

        Assert.AreEqual(1.5, session.Alignment.Speed, 1e-9);
        Assert.AreSequenceEqual([1440L, 2080L], Ticks(session));
    }

    [TestMethod]
    public void AClickAndTheMiddleAndRightButtonsChangeNothing()
    {
        var session = Aligning(Song());

        Click(session, At(session, 1500, 0));
        session.Tools.PressErase(At(session, 960, 0));
        session.Tools.Release(At(session, 960, 0));
        session.Tools.PressContext(At(session, 960, 0));
        session.Tools.Release(At(session, 1100, 0));

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
    }

    [TestMethod]
    public void ChordMarkingsThatLandOnOneAnotherAreKeptOnceAndTheStatusSaysSo()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(960, 0, 0), new EditableNote(961, 0, 1), new EditableNote(962, 0, 2)]).Apply(chart);
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [961, 962], ChordFlags.ForceHopo, on: true).Apply(chart);
        var session = Aligning(chart);

        // At 150% the notes in lanes 1 and 2 both go to 961, as a chord, and so do both markings.
        Assert.IsTrue(session.Alignment.Apply(0, 1.5));

        Assert.AreEqual(new FlaggedChord(961, ChordFlags.ForceHopo), Track(session, GuitarExpert).FlaggedChords.Single());
        Assert.IsNotNull(session.Status);
        Assert.Contains("chord marking", session.Status);
    }

    [TestMethod]
    public void APressInADifficultyWithNoNotesTakesHoldOfNothing()
    {
        var session = Aligning(Song());
        var easy = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Easy);
        session.Focused = easy;
        Assert.IsFalse(session.Alignment.Aligns(easy));
        Assert.IsNull(session.Tools.AlignGripAt(At(session, 1500, 0, key: easy)));

        DragFromTo(session, At(session, 1500, 0, key: easy), At(session, 1977, 0, key: easy));

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void AnotherInstrumentTakingFocusKeepsAChangeStillShownOnThePartItWasMadeTo()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(960, 0, 0)]).Apply(chart);
        new AddNotes(ChartInstrument.Bass, ChartDifficulty.Expert, [new EditableNote(1920, 0, 0)]).Apply(chart);
        var bass = new ChartTrackKey(ChartInstrument.Bass, ChartDifficulty.Expert);
        var session = Aligning(chart);

        // As a speed typed in the toolbar shows before it's settled.
        session.Alignment.Preview(0.5, 1, by: this);
        session.Focused = bass;

        Assert.AreSequenceEqual([1440L], Ticks(session));
        Assert.AreSequenceEqual([1920L], Ticks(session, bass));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsFalse(session.Alignment.IsPreviewing);
        Assert.IsTrue(session.Alignment.Aligns(bass));
        Assert.AreEqual((0.0, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        Assert.AreEqual((2.0, 2.0), session.Alignment.Span);
    }

    [TestMethod]
    public void APressKeepsAChangeStillShownAndTheDragIsItsOwn()
    {
        var session = AligningWithSnapOff(Song());
        session.Alignment.Preview(0.5, 1, by: this);

        session.Tools.Press(At(session, 1980, 0));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Tools.Drag(At(session, 2460, 0));

        // The drag's preview isn't the fields' to keep, as their wait ends.
        Assert.IsTrue(session.Alignment.IsPreviewing);
        Assert.AreNotSame(this, session.Alignment.PreviewedBy);
        session.Tools.Release(At(session, 2460, 0));

        Assert.AreEqual(2, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([1920L, 2880L], Ticks(session));
    }

    [TestMethod]
    public void ALaneOfAnotherDifficultyTakingFocusAlignsItsOwnWithoutAllDifficulties()
    {
        var session = Aligning(Song());
        session.Alignment.AllDifficulties = false;

        session.Focused = GuitarHard;
        session.Alignment.Apply(0.5, 1);

        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session, GuitarHard));
    }
}
