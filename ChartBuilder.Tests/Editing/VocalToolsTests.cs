using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The tools in a vocal part's lane (charter-spec C3 item 7), run against the shared tools as the lane's canvas calls
/// them (AC C3.2): every edit one undoable step, a note's lyric going with it.
/// </summary>
[TestClass]
public sealed class VocalToolsTests
{
    // A grid step at the default snap, 1/16, at 480 ticks a beat.
    private const int Step = 120;

    // Placing.

    [TestMethod]
    public void PlaceAddsANoteAGridStepLongOnTheGridAndRowAsOneStep()
    {
        var session = Of(WithVocals([]));
        session.Tool = EditorTool.Place;

        Click(session, VocalAt(130, 64));

        Assert.AreEqual(new VocalNoteEvent(120, Step, 64), Vocals(session).Notes.Single());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual(new[] { new VocalNotePlace(120, 64) }, [.. session.Selection.VocalPlaces]);
    }

    [TestMethod]
    public void DraggingPlacesALongerNoteThatStopsAtTheNextNotesStart()
    {
        var session = Of(WithVocals([new(480, 120, 50)]));
        session.Tool = EditorTool.Place;

        DragFromTo(session, VocalAt(0, 60), VocalAt(900, 60));

        Assert.AreEqual(new VocalNoteEvent(0, 480, 60), Vocals(session).Notes[0]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ShiftPlacesASlide()
    {
        var session = Of(WithVocals([new(0, 240, 60)], [new(0, "go")]));
        session.Tool = EditorTool.Place;

        Click(session, VocalAt(240, 64), EditorModifiers.Shift);

        Assert.AreEqual("+", VocalEdits.LyricAt(Vocals(session), 240));
        Assert.IsTrue(session.VocalsOf(VocalTrack.Vocals)!.Notes[1].IsSlide);
    }

    [TestMethod]
    public void UndoPutsBackTheTrackAsItWas()
    {
        var session = Of(WithVocals([new(0, 240, 60)]));
        var before = session.Chart.Passthrough.Single(t => t.Name == "PART VOCALS");
        session.Tool = EditorTool.Place;
        Click(session, VocalAt(480, 62));

        session.Undo();

        Assert.AreSame(before, session.Chart.Passthrough.Single(t => t.Name == "PART VOCALS"));
        Assert.HasCount(1, session.VocalsOf(VocalTrack.Vocals)!.Notes);
    }

    // Selecting and moving.

    [TestMethod]
    public void ClickSelectsANoteAndShiftClickAddsOrTakesOneAway()
    {
        var session = Of(WithVocals([new(0, 240, 60), new(480, 240, 62)]));

        Click(session, VocalAt(100, 60));
        Click(session, VocalAt(500, 62), EditorModifiers.Shift);
        Assert.HasCount(2, session.Selection.VocalPlaces);

        Click(session, VocalAt(100, 60), EditorModifiers.Shift);
        Assert.AreSequenceEqual(new[] { new VocalNotePlace(480, 62) }, [.. session.Selection.VocalPlaces]);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARowEitherSideStillHitsANoteWhenNoneIsOnThePointersOwn()
    {
        var session = Of(WithVocals([new(0, 240, 60)]));

        Click(session, VocalAt(100, 61));

        Assert.HasCount(1, session.Selection.VocalPlaces);
    }

    [TestMethod]
    public void DraggingASelectedNoteMovesItInTimeAndPitchWithItsLyric()
    {
        var session = Of(WithVocals([new(0, 240, 60)], [new(0, "word")]));

        DragFromTo(session, VocalAt(10, 60), VocalAt(490, 63));

        var content = Vocals(session);
        Assert.AreEqual(new VocalNoteEvent(480, 240, 63), content.Notes.Single());
        Assert.AreEqual(new VocalLyricEvent(480, "word"), content.Lyrics.Single());
        Assert.AreSequenceEqual(new[] { new VocalNotePlace(480, 63) }, [.. session.Selection.VocalPlaces]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AClickLeavesANoteOffTheGridWhereItIs()
    {
        var session = Of(WithVocals([new(37, 200, 60)]));

        Click(session, VocalAt(40, 60));

        Assert.AreEqual(37, Vocals(session).Notes.Single().Tick);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AMoveStopsWhereANoteWouldGoPastTheHighestSungPitch()
    {
        var session = Of(WithVocals([new(0, 240, 60), new(480, 240, 83)]));
        session.Tools.SelectAllVocals(VocalTrack.Vocals);

        DragFromTo(session, VocalAt(10, 60), VocalAt(10, 64));

        Assert.AreSequenceEqual(new[] { 61, 84 }, [.. Vocals(session).Notes.Select(n => n.Pitch)]);
    }

    [TestMethod]
    public void DraggingANotesEndChangesItsLength()
    {
        var session = Of(WithVocals([new(0, 240, 60)]));

        DragFromTo(session, VocalAt(238, 60), VocalAt(470, 60));

        Assert.AreEqual(new VocalNoteEvent(0, 480, 60), Vocals(session).Notes.Single());
    }

    [TestMethod]
    public void ABoxSelectsTheNotesInItsTicksAndRows()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 64), new(480, 100, 70), new(960, 100, 62)]));

        DragFromTo(session, VocalAt(0, 58), VocalAt(500, 66));

        Assert.AreSequenceEqual(new[] { new VocalNotePlace(0, 60), new VocalNotePlace(240, 64) }, [.. session.Selection.VocalPlaces.OrderBy(p => p.Tick)]);
    }

    [TestMethod]
    public void TheSelectAllAndMoveKeysActOnTheVocalNotes()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 83)]));

        session.Tools.SelectAllVocals(VocalTrack.Vocals);
        session.Tools.MoveVocalSelection(1, 5);

        Assert.AreSequenceEqual(new[] { new VocalNoteEvent(120, 100, 61), new VocalNoteEvent(360, 100, 84) }, [.. Vocals(session).Notes]);
        Assert.HasCount(2, session.Selection.VocalPlaces);
    }

    [TestMethod]
    public void CancellingADragLeavesThePartAndTheSelectionAsTheyWere()
    {
        var session = Of(WithVocals([new(0, 240, 60)]));
        var before = Vocals(session);

        session.Tools.Press(VocalAt(10, 60));
        session.Tools.Drag(VocalAt(490, 65));
        session.Tools.Cancel();

        Assert.AreSame(before, Vocals(session));
        Assert.AreSequenceEqual(new[] { new VocalNotePlace(0, 60) }, [.. session.Selection.VocalPlaces]);
    }

    // Erasing and deleting.

    [TestMethod]
    public void EraseRemovesEachNotePassedOverWithItsLyricAsOneStep()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 60), new(480, 100, 60)], [new(0, "a"), new(240, "b"), new(480, "c")]));
        session.Tool = EditorTool.Erase;

        DragFromTo(session, VocalAt(10, 60), VocalAt(250, 60));

        var content = Vocals(session);
        Assert.AreEqual(480, content.Notes.Single().Tick);
        Assert.AreEqual("c", content.Lyrics.Single().Text);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void DeleteRemovesTheSelectedNotesAndPhrasesAsOneStep()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 62)], [new(0, "a"), new(240, "b")], [new(0, 960), new(1920, 480)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60)]);
        session.Selection.SelectVocalPhrases(VocalTrack.Vocals, [new(1920, 480)], add: true);

        session.Tools.DeleteSelection();

        var content = Vocals(session);
        Assert.AreEqual(240, content.Notes.Single().Tick);
        Assert.AreEqual("b", content.Lyrics.Single().Text);
        Assert.AreEqual(new VocalPhraseEvent(0, 960), content.Phrases.Single());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Selection.IsEmpty);
    }

    // Phrases.

    [TestMethod]
    public void ThePhraseToolDrawsAPhraseAndSelectsIt()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, VocalAt(5, 60), VocalAt(950, 60));

        Assert.AreEqual(new VocalPhraseEvent(0, 960), Vocals(session).Phrases.Single());
        Assert.AreSequenceEqual(new[] { new VocalPhraseEvent(0, 960) }, [.. session.Selection.VocalPhrases]);
    }

    [TestMethod]
    public void APhraseThatWouldOverlapAnotherIsRefused()
    {
        var session = Of(WithVocals([], phrases: [new(480, 480)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, PhraseStripAt(0), PhraseStripAt(720));

        Assert.AreEqual(new VocalPhraseEvent(480, 480), Vocals(session).Phrases.Single());
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void APhraseBarIsSelectedMovedAndResizedInTheStripWithAnyTool()
    {
        const VocalPhraseMarkers both = VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary;
        var session = Of(WithVocals([], phrases: [new(0, 960, both)]));
        session.Tool = EditorTool.Erase;

        DragFromTo(session, PhraseStripAt(480), PhraseStripAt(960));
        Assert.AreEqual(new VocalPhraseEvent(480, 960, both), Vocals(session).Phrases.Single());

        DragFromTo(session, PhraseStripAt(1440), PhraseStripAt(1920));
        Assert.AreEqual(new VocalPhraseEvent(480, 1440, both), Vocals(session).Phrases.Single());
        Assert.AreSequenceEqual(new[] { new VocalPhraseEvent(480, 1440, both) }, [.. session.Selection.VocalPhrases]);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ANewPhraseIsWrittenAsTheOthersAre()
    {
        const VocalPhraseMarkers both = VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary;
        var session = Of(WithVocals([], phrases: [new(0, 480, both)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, PhraseStripAt(960), PhraseStripAt(1440));

        Assert.AreEqual(both, Vocals(session).Phrases[1].Markers);
    }

    // The tempo tool.

    [TestMethod]
    public void TheTempoToolAddsAMarkerFromAVocalLane()
    {
        var session = Of(WithVocals([]));
        session.Tool = EditorTool.Tempo;

        Click(session, VocalAt(965, 60));

        Assert.IsFalse(session.Chart.TempoMap.Tempos.At(960).IsEmpty);
        Assert.AreSequenceEqual(new long[] { 960 }, [.. session.Selection.TempoTicks]);
    }

    [TestMethod]
    public void TheTempoToolDragsATimeSignatureFromAVocalLane()
    {
        var session = Of(WithVocals([]));
        session.Chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(960, 3, 4));
        session.Tool = EditorTool.Tempo;

        DragFromTo(session, VocalAt(962, 60), VocalAt(1440, 60));

        Assert.IsTrue(session.Chart.TempoMap.TimeSignatures.At(960).IsEmpty);
        Assert.HasCount(1, session.Chart.TempoMap.TimeSignatures.At(1440));
    }

    // Lyrics, slides and spoken notes.

    [TestMethod]
    public void TypedLyricsGoToTheSelectedNotesInTurnSkippingASlide()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(120, 100, 62), new(240, 100, 64), new(360, 100, 65)], [new(120, "+")]));
        session.Tools.SelectAllVocals(VocalTrack.Vocals);

        int? left = session.Tools.SpreadVocalLyrics("Hel-lo world again");

        Assert.AreEqual(1, left);
        Assert.AreSequenceEqual(
            new[] { new VocalLyricEvent(0, "Hel-"), new VocalLyricEvent(120, "+"), new VocalLyricEvent(240, "lo"), new VocalLyricEvent(360, "world") },
            [.. Vocals(session).Lyrics]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheLyricSlideAndSpokenOfTheSelectedNotesAreSet()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 62)], [new(0, "hey-")]));
        session.Tools.SelectAllVocals(VocalTrack.Vocals);

        session.Tools.SetVocalSpoken(true);
        Assert.AreSequenceEqual(new[] { "hey-#", "#" }, [.. Vocals(session).Lyrics.Select(l => l.Text)]);

        session.Tools.SetVocalSpoken(false);
        Assert.AreSequenceEqual(new[] { "hey-" }, [.. Vocals(session).Lyrics.Select(l => l.Text)]);

        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(240, 62)]);
        session.Tools.SetVocalSlide(true);
        session.Tools.SetVocalPitch(70);
        Assert.AreEqual(new VocalLyricEvent(240, "+"), Vocals(session).Lyrics[1]);
        Assert.AreEqual(70, Vocals(session).Notes[1].Pitch);

        session.Tools.SetVocalLyric("");
        Assert.HasCount(1, Vocals(session).Lyrics);
    }

    // The selection.

    [TestMethod]
    public void SelectingAVocalPartsNotesDropsAHighwaysAndTheOtherWayRound()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(session.Chart);

        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60)]);
        Assert.IsFalse(session.Selection.HasNotes);

        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        Assert.IsFalse(session.Selection.HasVocalNotes);
        Assert.IsNull(session.Selection.Vocal);
    }

    [TestMethod]
    public void AnUndoDropsTheVocalNotesItTakesAwayFromTheSelection()
    {
        var session = Of(WithVocals([]));
        session.Tool = EditorTool.Place;
        Click(session, VocalAt(0, 60));
        Assert.HasCount(1, session.Selection.VocalPlaces);

        session.Undo();

        Assert.IsEmpty(session.Selection.VocalPlaces);
        Assert.IsTrue(session.Selection.IsEmpty);
    }

    [TestMethod]
    public void TheSpanSelectedCoversTheVocalNotes()
    {
        var session = Of(WithVocals([new(240, 100, 60), new(720, 240, 62)]));

        session.Tools.SelectAllVocals(VocalTrack.Vocals);

        Assert.AreEqual((240L, 960L), session.Selection.Span);
    }

    // The clipboard.

    [TestMethod]
    public void VocalNotesCopyAndPasteIntoAnotherVocalPartWithTheirLyrics()
    {
        var chart = WithVocals([new(0, 100, 60), new(240, 100, 62)], [new(0, "Hel-"), new(240, "lo")]);
        var session = Of(WithVocals([new(1000, 100, 50)], [new(1000, "x")], track: VocalTrack.Harmony1, chart: chart));
        session.Tools.SelectAllVocals(VocalTrack.Vocals);
        var clip = ClipboardEdits.Copy(session)!;
        session.MoveCursorTo(960);

        Assert.IsNotNull(ClipboardEdits.CanPaste(session, clip, out _));
        Assert.IsNull(ClipboardEdits.CanPaste(session, clip, VocalTrack.Harmony1, out _));
        var paste = ClipboardEdits.PasteVocals(session, clip, VocalTrack.Harmony1, session.CursorTick, PasteMode.Overwrite);
        Assert.IsTrue(session.Execute(paste.Command!));

        var harmony = Vocals(session, VocalTrack.Harmony1);
        Assert.AreSequenceEqual(new[] { new VocalNoteEvent(960, 100, 60), new VocalNoteEvent(1200, 100, 62) }, [.. harmony.Notes]);
        Assert.AreSequenceEqual(new[] { "Hel-", "lo" }, [.. harmony.Lyrics.Select(l => l.Text)]);
        Assert.AreSequenceEqual(new[] { new VocalNotePlace(960, 60), new VocalNotePlace(1200, 62) }, [.. paste.Placed]);
    }

    [TestMethod]
    public void AMergingPasteKeepsTheNotesBetweenThePastedOnes()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(480, 100, 62)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60)]);
        var clip = ClipboardEdits.Copy(session)! with { Length = 960 };

        var merge = ClipboardEdits.PasteVocals(session, clip, VocalTrack.Vocals, 240, PasteMode.Merge);
        var overwrite = ClipboardEdits.PasteVocals(session, clip, VocalTrack.Vocals, 240, PasteMode.Overwrite);

        Assert.IsTrue(session.Execute(merge.Command!));
        Assert.HasCount(3, Vocals(session).Notes);
        session.Undo();
        Assert.IsTrue(session.Execute(overwrite.Command!));
        Assert.HasCount(2, Vocals(session).Notes);
    }

    [TestMethod]
    public void AHighwaysNotesDontPasteIntoAVocalPartNorTheOtherWayRound()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(session.Chart);
        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        var highway = ClipboardEdits.Copy(session)!;
        session.Tools.SelectAllVocals(VocalTrack.Vocals);
        var vocals = ClipboardEdits.Copy(session)!;

        Assert.IsNotNull(ClipboardEdits.CanPaste(session, highway, VocalTrack.Vocals, out _));
        Assert.IsNotNull(ClipboardEdits.Paste(session, vocals, 0, PasteMode.Overwrite).Refusal);
        Assert.IsNotNull(ClipboardEdits.PasteVocals(session, vocals, VocalTrack.Harmony2, 0, PasteMode.Overwrite).Refusal);
    }
}
