using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Phrases in the editor (charter-spec C8, C3 item 4, §9): the Phrase tool and the kinds it draws, the phrase rail's
/// selecting, moving, resizing and deleting, the S key, and the phrase fields of Properties, as the tools carry them out.
/// </summary>
[TestClass]
public sealed class PhraseEditingTests
{
    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    public static IEnumerable<object[]> FretKinds =>
        from view in new[] { LaneView.Highway, LaneView.Timeline }
        from kind in new[] { PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding }
        select new object[] { view, kind };

    public static IEnumerable<object[]> DrumKinds =>
        from view in new[] { LaneView.Highway, LaneView.Timeline }
        from kind in new[] { PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.DrumFill, PhraseKind.Roll, PhraseKind.Trill }
        select new object[] { view, kind };

    // The Phrase tool.

    [TestMethod]
    [DynamicData(nameof(FretKinds))]
    public void DraggingWithThePhraseToolAddsAPhraseOfTheKindPickedOnTheGridAsOneStep(LaneView view, PhraseKind kind)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = kind;

        DragFromTo(session, At(session, 130, 1, view), At(session, 950, 3, view));

        var drawn = new Phrase(kind, 120, 840);
        Assert.AreSequenceEqual([drawn], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([drawn], session.Selection.Phrases.ToList());
        session.Undo();
        Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void APhraseIsDrawnBackwardsAsWellAsForwards(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, At(session, 950, 2, view), At(session, 130, 2, view));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 120, 840)], Phrases(session, ChartInstrument.Guitar));
    }

    [TestMethod]
    [DynamicData(nameof(DrumKinds))]
    public void EveryDrumKindIsDrawnAndRollsAndTrillsReachExpertOnly(LaneView view, PhraseKind kind)
    {
        // C6 item 6: new rolls and trills reach Expert only.
        var session = Of(Drums(), focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = kind;

        DragFromTo(session, At(session, 0, 1, view, DrumsExpert), At(session, 480, 2, view, DrumsExpert));

        ChartDifficulty? lowest = kind is PhraseKind.Roll or PhraseKind.Trill ? ChartDifficulty.Expert : null;
        Assert.AreSequenceEqual([new Phrase(kind, 0, 480, lowest)], Phrases(session, ChartInstrument.Drums));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ADrumFillDrawnAtOrAfterCodaIsABigRockEnding(LaneView view)
    {
        // C8 item 4: on drums, a big rock ending is the fill markers after [coda], so it's never refused as a fill.
        var chart = Drums();
        new AddTextEvent(null, TextEvent.Of(1920, "[coda]")).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = PhraseKind.DrumFill;

        DragFromTo(session, At(session, 0, 1, view, DrumsExpert), At(session, 960, 1, view, DrumsExpert));
        DragFromTo(session, At(session, 1920, 1, view, DrumsExpert), At(session, 2880, 1, view, DrumsExpert));

        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.DrumFill, 0, 960), new Phrase(PhraseKind.BigRockEnding, 1920, 960)],
            Phrases(session, ChartInstrument.Drums));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AClickWithThePhraseToolAddsNothingAndSelectsNoPhrases(LaneView view)
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.Solo, 0, 960);
        Add(session, ChartInstrument.Guitar, phrase);
        session.Selection.SelectPhrases(ChartInstrument.Guitar, [phrase]);
        session.Tool = EditorTool.Phrase;

        Click(session, At(session, 1500, 1, view));

        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsFalse(session.Selection.HasPhrases);
    }

    [TestMethod]
    public void AKindTheFocusedPartCantHaveIsRefusedInTheStatus()
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = PhraseKind.Roll;

        DragFromTo(session, At(session, 0, 1), At(session, 480, 1));

        Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void EachPartIsOfferedTheKindsItCanHave()
    {
        Assert.AreSequenceEqual(
            [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding],
            PhraseRail.KindsOf(ChartInstrumentKind.FiveFret).ToList());
        Assert.AreSequenceEqual(
            [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding],
            PhraseRail.KindsOf(ChartInstrumentKind.SixFret).ToList());
        Assert.AreSequenceEqual(
            [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.DrumFill, PhraseKind.Roll, PhraseKind.Trill],
            PhraseRail.KindsOf(ChartInstrumentKind.Drums).ToList());
        Assert.AreEqual(PhraseKind.DrumFill, PhraseRail.RowOf(ChartInstrumentKind.Drums, PhraseKind.BigRockEnding));
        Assert.AreEqual(PhraseKind.BigRockEnding, PhraseRail.RowOf(ChartInstrumentKind.FiveFret, PhraseKind.BigRockEnding));
    }

    [TestMethod]
    public void AKindTheNewlyFocusedPartCantHaveFallsBackToStarPower()
    {
        var chart = Drums();
        chart.AddPart(ChartInstrument.Guitar);
        var session = Of(chart, focused: DrumsExpert);

        session.PhraseKind = PhraseKind.Solo;
        session.Focused = GuitarExpert;
        Assert.AreEqual(PhraseKind.Solo, session.PhraseKind, "Both parts can have solos.");

        session.Focused = DrumsExpert;
        session.PhraseKind = PhraseKind.Trill;
        session.Focused = GuitarExpert;
        Assert.AreEqual(PhraseKind.StarPower, session.PhraseKind);
    }

    // The phrase rail.

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void PressingABarInTheRailSelectsItsPhraseAndShiftAddsOrTakesAway(LaneView view)
    {
        var session = Of(Guitar());
        var starPower = new Phrase(PhraseKind.StarPower, 0, 960);
        var solo = new Phrase(PhraseKind.Solo, 480, 960);
        Add(session, ChartInstrument.Guitar, starPower, solo);
        Expert(session).Notes.Add(new EditableNote(480, 1, 1));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);

        // Every tool selects in the rail.
        session.Tool = EditorTool.Erase;
        Click(session, RailAt(session, 600, PhraseKind.StarPower, view));
        Assert.AreSequenceEqual([starPower], session.Selection.Phrases.ToList());
        Assert.IsFalse(session.Selection.HasNotes, "Selecting a phrase drops the notes selected.");

        Click(session, RailAt(session, 600, PhraseKind.Solo, view), EditorModifiers.Shift);
        Assert.AreSequenceEqual([starPower, solo], session.Selection.Phrases.ToList());

        Click(session, RailAt(session, 600, PhraseKind.StarPower, view), EditorModifiers.Shift);
        Assert.AreSequenceEqual([solo], session.Selection.Phrases.ToList());

        Click(session, RailAt(session, 3000, PhraseKind.StarPower, view));
        Assert.IsFalse(session.Selection.HasPhrases);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void SelectingNotesOrTemposDropsThePhrasesAndTheOtherWayRound()
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.StarPower, 0, 960);
        Add(session, ChartInstrument.Guitar, phrase);
        var note = new EditableNote(480, 1, 1);
        Expert(session).Notes.Add(note);
        var selection = session.Selection;

        selection.SelectPhrases(ChartInstrument.Guitar, [phrase]);
        selection.Select(GuitarExpert, [note]);
        Assert.IsFalse(selection.HasPhrases);

        selection.SelectPhrases(ChartInstrument.Guitar, [phrase]);
        Assert.IsFalse(selection.HasNotes);

        selection.SelectTempos([0]);
        Assert.IsFalse(selection.HasPhrases);

        selection.Select(GuitarExpert, [note]);
        selection.SelectPhrases(ChartInstrument.Guitar, [phrase], add: true);
        Assert.IsTrue(selection.HasNotes && selection.HasPhrases, "Adding keeps both.");

        selection.SelectPhrases(ChartInstrument.Guitar, [phrase]);
        Assert.AreEqual((0L, 960L), selection.Span, "A phrase selected alone spans its ticks.");
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingABarsBodyMovesItsPhraseToTheGridAsOneStep(LaneView view)
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.StarPower, 960, 960);
        Add(session, ChartInstrument.Guitar, phrase);

        DragFromTo(session, RailAt(session, 1440, PhraseKind.StarPower, view), RailAt(session, 1700, PhraseKind.StarPower, view));

        var moved = new Phrase(PhraseKind.StarPower, 1200, 960);
        Assert.AreSequenceEqual([moved], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([moved], session.Selection.Phrases.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingSeveralSelectedPhrasesMovesThemTogetherAndNeverBeforeTheStart(LaneView view)
    {
        var session = Of(Guitar());
        var first = new Phrase(PhraseKind.StarPower, 240, 480);
        var second = new Phrase(PhraseKind.Solo, 960, 480);
        Add(session, ChartInstrument.Guitar, first, second);
        session.Selection.SelectPhrases(ChartInstrument.Guitar, [first, second]);

        DragFromTo(session, RailAt(session, 1200, PhraseKind.Solo, view), RailAt(session, 0, PhraseKind.Solo, view));

        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.StarPower, 0, 480), new Phrase(PhraseKind.Solo, 720, 480)],
            Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingABarsEndsResizesItsPhraseToTheGrid(LaneView view)
    {
        var session = Of(Guitar());
        Add(session, ChartInstrument.Guitar, new Phrase(PhraseKind.Solo, 960, 960));

        DragFromTo(session, RailAt(session, 1915, PhraseKind.Solo, view), RailAt(session, 2390, PhraseKind.Solo, view));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.Solo, 960, 1440)], Phrases(session, ChartInstrument.Guitar));

        DragFromTo(session, RailAt(session, 962, PhraseKind.Solo, view), RailAt(session, 1450, PhraseKind.Solo, view));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.Solo, 1440, 960)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(2, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new Phrase(PhraseKind.Solo, 1440, 960)], session.Selection.Phrases.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AStartDraggedPastTheEndStopsAGridStepShortOfIt(LaneView view)
    {
        var session = Of(Guitar());
        Add(session, ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 960, 960));

        DragFromTo(session, RailAt(session, 960, PhraseKind.StarPower, view), RailAt(session, 3000, PhraseKind.StarPower, view));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 1800, 120)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AnEndDraggedPastTheStartStopsAGridStepShortOfIt(LaneView view)
    {
        var session = Of(Guitar());
        Add(session, ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 960, 960));

        DragFromTo(session, RailAt(session, 1920, PhraseKind.StarPower, view), RailAt(session, 0, PhraseKind.StarPower, view));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 960, 120)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ClickingABarsEndOrBodyOnlySelectsItsPhrase(LaneView view)
    {
        // S puts a plain last note's end a tick past it, off the grid: a click on the bar mustn't snap it back.
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.Notes.Add(new EditableNote(960, 1, 2));
        session.Selection.Select(GuitarExpert, track.Notes);
        session.Tools.ToggleStarPowerOverSelection();
        var phrase = new Phrase(PhraseKind.StarPower, 480, 481);

        Click(session, RailAt(session, 961, PhraseKind.StarPower, view));
        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar));
        Assert.AreSequenceEqual([phrase], session.Selection.Phrases.ToList());

        Click(session, RailAt(session, 485, PhraseKind.StarPower, view));
        Click(session, RailAt(session, 720, PhraseKind.StarPower, view));
        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsFalse(session.HasPreview);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ABarIsMovedOnlyOnceThePointerLeavesItsTolerance(LaneView view)
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.Solo, 485, 960);
        Add(session, ChartInstrument.Guitar, phrase);

        DragFromTo(session, RailAt(session, 900, PhraseKind.Solo, view), RailAt(session, 908, PhraseKind.Solo, view));
        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar), "A start off the grid stays put through a nudge.");

        DragFromTo(session, RailAt(session, 900, PhraseKind.Solo, view), RailAt(session, 1140, PhraseKind.Solo, view));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.Solo, 720, 960)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AFillMovedPastCodaBecomesABigRockEnding(LaneView view)
    {
        var chart = Drums();
        new AddTextEvent(null, TextEvent.Of(1920, "[coda]")).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        Add(session, ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 0, 480));

        DragFromTo(session, RailAt(session, 240, PhraseKind.DrumFill, view, DrumsExpert), RailAt(session, 2160, PhraseKind.DrumFill, view, DrumsExpert));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.BigRockEnding, 1920, 480)], Phrases(session, ChartInstrument.Drums));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AFillDraggedAcrossCodaAndBackIsReclassifiedAsItGoes(LaneView view)
    {
        var chart = Drums();
        new AddTextEvent(null, TextEvent.Of(1920, "[coda]")).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        Add(session, ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 0, 480));

        session.Tools.Press(RailAt(session, 240, PhraseKind.DrumFill, view, DrumsExpert));
        session.Tools.Drag(RailAt(session, 2160, PhraseKind.DrumFill, view, DrumsExpert));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.BigRockEnding, 1920, 480)], Phrases(session, ChartInstrument.Drums));
        Assert.IsNull(session.Status);

        session.Tools.Drag(RailAt(session, 600, PhraseKind.DrumFill, view, DrumsExpert));
        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 360, 480)], Phrases(session, ChartInstrument.Drums));
        session.Tools.Release(RailAt(session, 600, PhraseKind.DrumFill, view, DrumsExpert));

        Assert.IsNull(session.Status);
        Assert.IsFalse(session.HasPreview);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 360, 480)], session.Selection.Phrases.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AMoveOrResizeRefusedMidDragLeavesNoPreviewAndSaysWhy(LaneView view)
    {
        // The phrase grabbed goes from under the gesture, so ChangePhrase refuses it.
        foreach (double grabbed in (double[])[1440, 1920])
        {
            var session = Of(Guitar());
            var phrase = new Phrase(PhraseKind.StarPower, 960, 960);
            Add(session, ChartInstrument.Guitar, phrase);

            session.Tools.Press(RailAt(session, grabbed, PhraseKind.StarPower, view));
            new RemovePhrase(ChartInstrument.Guitar, phrase).Apply(session.Chart);
            session.Tools.Drag(RailAt(session, grabbed + 480, PhraseKind.StarPower, view));

            Assert.IsFalse(session.HasPreview);
            Assert.IsNotNull(session.Status);
            session.Tools.Release(RailAt(session, grabbed + 480, PhraseKind.StarPower, view));
            Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
            Assert.AreEqual(0, session.UndoStack.UndoCount);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ThePhraseToolDrawsNothingFromOutsideTheLanesAndTheRail(LaneView view)
    {
        // The labels' margin, the waveform's strip, the forcing marks' gap: no lane and no rail row.
        var session = Of(Guitar());
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, At(session, 0, null, view), At(session, 960, null, view));

        Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheRailHitsABarsEndsWithinTheToleranceAndItsBodyBetween()
    {
        var session = Of(Guitar());
        var part = session.Chart.PartOf(ChartInstrument.Guitar)!;
        var outer = new Phrase(PhraseKind.StarPower, 0, 1920);
        var inner = new Phrase(PhraseKind.StarPower, 480, 480);
        Add(session, ChartInstrument.Guitar, outer, inner, new Phrase(PhraseKind.Solo, 0, 3840));

        Assert.AreEqual(new PhraseHit(outer, PhraseGrip.Start), PhraseRail.HitTest(part, RailAt(session, 5, PhraseKind.StarPower)));
        Assert.AreEqual(new PhraseHit(outer, PhraseGrip.End), PhraseRail.HitTest(part, RailAt(session, 1925, PhraseKind.StarPower)));
        Assert.AreEqual(new PhraseHit(outer, PhraseGrip.Body), PhraseRail.HitTest(part, RailAt(session, 1200, PhraseKind.StarPower)));
        Assert.AreEqual(new PhraseHit(inner, PhraseGrip.Body), PhraseRail.HitTest(part, RailAt(session, 700, PhraseKind.StarPower)), "The shorter phrase inside comes first.");
        Assert.IsNull(PhraseRail.HitTest(part, RailAt(session, 2000, PhraseKind.StarPower)));
        Assert.AreEqual(PhraseKind.Solo, PhraseRail.HitTest(part, RailAt(session, 2000, PhraseKind.Solo))?.Phrase.Kind);
        Assert.IsNull(PhraseRail.HitTest(part, At(session, 700, 1)), "A point in the lanes isn't in the rail.");
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ThePhraseToolDrawsInAnEmptyPartOfTheRail(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = PhraseKind.Solo;

        DragFromTo(session, RailAt(session, 480, PhraseKind.StarPower, view), RailAt(session, 960, PhraseKind.StarPower, view));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.Solo, 480, 480)], Phrases(session, ChartInstrument.Guitar));
    }

    [TestMethod]
    public void DeleteRemovesTheSelectedPhrasesWithTheNotesAsOneStep()
    {
        var session = Of(Guitar());
        var starPower = new Phrase(PhraseKind.StarPower, 0, 960);
        var solo = new Phrase(PhraseKind.Solo, 0, 960);
        Add(session, ChartInstrument.Guitar, starPower, solo);
        var note = new EditableNote(480, 1, 1);
        Expert(session).Notes.Add(note);
        session.Selection.Select(GuitarExpert, [note]);
        session.Selection.SelectPhrases(ChartInstrument.Guitar, [starPower], add: true);

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual([solo], Phrases(session, ChartInstrument.Guitar));
        Assert.IsEmpty(Expert(session).Notes);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Selection.IsEmpty);
        session.Undo();
        Assert.AreSequenceEqual([starPower, solo], Phrases(session, ChartInstrument.Guitar));
        Assert.HasCount(1, Expert(session).Notes);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheSelectionDropsAPhraseUndoneAway(LaneView view)
    {
        var session = Of(Guitar());
        Add(session, ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 960, 960));
        DragFromTo(session, RailAt(session, 1440, PhraseKind.StarPower, view), RailAt(session, 1920, PhraseKind.StarPower, view));
        Assert.IsTrue(session.Selection.HasPhrases);

        session.Undo();

        Assert.IsFalse(session.Selection.HasPhrases);
        Assert.IsNull(session.Selection.PhrasePart);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void EscapingAMoveLeavesThePhraseAndItsSelection(LaneView view)
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.StarPower, 960, 960);
        Add(session, ChartInstrument.Guitar, phrase);

        session.Tools.Press(RailAt(session, 1440, PhraseKind.StarPower, view));
        session.Tools.Drag(RailAt(session, 2400, PhraseKind.StarPower, view));
        session.Tools.Cancel();

        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar));
        Assert.AreSequenceEqual([phrase], session.Selection.Phrases.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // The S key.

    [TestMethod]
    public void SPutsStarPowerOverTheSelectedNotesToTheEndOfTheLastToEnd()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.Notes.Add(new EditableNote(960, 480, 2));
        track.Notes.Add(new EditableNote(1200, 1, 0));
        session.Selection.Select(GuitarExpert, track.Notes);

        session.Tools.ToggleStarPowerOverSelection();

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 480, 960)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Selection.HasNotes, "The notes stay selected, so S again takes it away.");
    }

    [TestMethod]
    public void SOverPlainNotesCoversTheLastOneByATick()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.Notes.Add(new EditableNote(960, 1, 2));
        session.Selection.Select(GuitarExpert, track.Notes);

        session.Tools.ToggleStarPowerOverSelection();

        var phrase = Phrases(session, ChartInstrument.Guitar).Single();
        Assert.AreEqual(new Phrase(PhraseKind.StarPower, 480, 481), phrase);
        Assert.IsTrue(phrase.Covers(960));
    }

    [TestMethod]
    public void SAgainTakesAwayTheStarPowerItPut()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 240, 1));
        session.Selection.Select(GuitarExpert, track.Notes);
        var other = new Phrase(PhraseKind.StarPower, 480, 960);
        Add(session, ChartInstrument.Guitar, other);

        session.Tools.ToggleStarPowerOverSelection();
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 480, 240), other], Phrases(session, ChartInstrument.Guitar));

        session.Tools.ToggleStarPowerOverSelection();
        Assert.AreSequenceEqual([other], Phrases(session, ChartInstrument.Guitar), "Only a phrase exactly over the selection is taken away.");
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void SPutsStarPowerOverTheSpanSelectedInTheFocusedPart()
    {
        var chart = Drums();
        chart.AddPart(ChartInstrument.Guitar);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.SetRange(480, 1920);

        session.Tools.ToggleStarPowerOverSelection();

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 480, 1440)], Phrases(session, ChartInstrument.Drums));
        Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
    }

    [TestMethod]
    public void SWithNothingSelectedSaysSo()
    {
        var session = Of(Guitar());

        session.Tools.ToggleStarPowerOverSelection();

        Assert.IsNotNull(session.Status);
        Assert.IsEmpty(Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // Properties.

    [TestMethod]
    public void SettingTheStartOrEndChangesEverySelectedPhraseAsOneStep()
    {
        var session = Of(Guitar());
        var first = new Phrase(PhraseKind.StarPower, 0, 960);
        var second = new Phrase(PhraseKind.Solo, 240, 1680);
        Add(session, ChartInstrument.Guitar, first, second);
        session.Selection.SelectPhrases(ChartInstrument.Guitar, [first, second]);

        session.Tools.SetPhraseEnd(1440);
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 0, 1440), new Phrase(PhraseKind.Solo, 240, 1200)], Phrases(session, ChartInstrument.Guitar));
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Tools.SetPhraseStart(480);
        var both = new[] { new Phrase(PhraseKind.StarPower, 480, 960), new Phrase(PhraseKind.Solo, 480, 960) };
        Assert.AreSequenceEqual(both, Phrases(session, ChartInstrument.Guitar));
        Assert.AreSequenceEqual(both, session.Selection.Phrases.ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AStartAtOrAfterTheEndIsRefused()
    {
        var session = Of(Guitar());
        var phrase = new Phrase(PhraseKind.StarPower, 0, 960);
        Add(session, ChartInstrument.Guitar, phrase);
        session.Selection.SelectPhrases(ChartInstrument.Guitar, [phrase]);

        session.Tools.SetPhraseStart(960);

        Assert.AreSequenceEqual([phrase], Phrases(session, ChartInstrument.Guitar));
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheLowestDifficultyExtendsTheSelectedRollsAndTrillsOnly()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var roll = new Phrase(PhraseKind.Roll, 0, 480, ChartDifficulty.Expert);
        var trill = new Phrase(PhraseKind.Trill, 960, 480, ChartDifficulty.Hard);
        var starPower = new Phrase(PhraseKind.StarPower, 0, 1920);
        Add(session, ChartInstrument.Drums, roll, trill, starPower);
        session.Selection.SelectPhrases(ChartInstrument.Drums, [roll, trill, starPower]);

        session.Tools.SetLowestDifficulty(ChartDifficulty.Medium);

        Assert.AreSequenceEqual(
            [starPower, roll with { LowestDifficulty = ChartDifficulty.Medium }, trill with { LowestDifficulty = ChartDifficulty.Medium }],
            Phrases(session, ChartInstrument.Drums));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ASolosNoteCountCountsAChordOnceAndLeavesOutItsEnd()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.Notes.Add(new EditableNote(0, 1, 1));
        track.Notes.Add(new EditableNote(480, 1, 2));
        track.Notes.Add(new EditableNote(960, 1, 2));

        Assert.AreEqual(2, PhraseRail.NoteCount(track, new Phrase(PhraseKind.Solo, 0, 960)));
        Assert.AreEqual(3, PhraseRail.NoteCount(track, new Phrase(PhraseKind.Solo, 0, 961)));
    }

    private static List<Phrase> Phrases(EditorSession session, ChartInstrument part) => [.. session.Chart.PartOf(part)!.Phrases];

    private static void Add(EditorSession session, ChartInstrument part, params Phrase[] phrases)
    {
        foreach (var phrase in phrases)
            new AddPhrase(part, phrase).Apply(session.Chart);
    }
}
