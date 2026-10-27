using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Disco flip in the editor (charter-spec C6 item 7, C8, C3 item 4): notes inside a section drawn and edited on the pads
/// the player hits, as the game view reads the flip, and the sections in the phrase rail, the Phrase tool and Properties,
/// as the tools carry them out.
/// </summary>
[TestClass]
public sealed class DiscoFlipEditingTests
{
    private const int Kick = 0, Red = 1, Yellow = 2, Blue = 3;

    private static readonly ChartTrackKey DrumsMedium = new(ChartInstrument.Drums, ChartDifficulty.Medium);

    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    // Which pad a note is drawn on.

    [TestMethod]
    public void InsideASectionRedAndYellowAreDrawnOnThePadsThePlayerHits()
    {
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(0, Red), Pad(480, Red), Pad(480, Yellow), Pad(480, Blue), Pad(480, Kick), Pad(960, Yellow));
        new AddDiscoFlip(ChartDifficulty.Expert, 480, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);

        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(0, Red)));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(480, Red)));
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(480, Yellow)));
        Assert.AreEqual(Blue, session.ShownLane(DrumsExpert, Pad(480, Blue)));
        Assert.AreEqual(Kick, session.ShownLane(DrumsExpert, Pad(480, Kick)));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(960, Yellow)), "The section ends before its end tick.");
        Assert.AreEqual(Red, session.StoredLane(DrumsExpert, 480, Yellow), "Yellow's column inside the section is red.");
        Assert.AreEqual(Yellow, session.StoredLane(DrumsExpert, 0, Yellow));
    }

    [TestMethod]
    public void EveryPadIsDrawnWhereTheGameViewPlaysIt()
    {
        // The editor can't disagree with the games: a pad note's drawn lane is the game view's, a red one flipped a yellow cymbal.
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(0, Red), Pad(0, Yellow), Pad(480, Red), Pad(600, Yellow), Pad(720, Blue), Pad(960, Red), Pad(1440, Yellow));
        new AddDiscoFlip(ChartDifficulty.Expert, 480, 960).Apply(chart);
        new SetToms([(Yellow, 600L)], tom: true).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        var game = session.GameTrack(DrumsExpert).Notes;

        foreach (var note in session.TrackOf(DrumsExpert)!.Notes)
        {
            int shown = session.ShownLane(DrumsExpert, note);
            Assert.Contains(g => g.Tick == note.Tick && g.Lane == shown - 1, game, $"The pad at {note.Tick} on lane {note.Lane} is drawn on {shown}.");
        }

        Assert.AreNotEqual(NoteFlags.None, game.Single(g => g.Tick == 480).Flags & NoteFlags.Cymbal, "Red inside the section is a yellow cymbal.");
        Assert.AreEqual(NoteFlags.None, game.Single(g => g.Tick == 600).Flags & NoteFlags.Cymbal, "Yellow inside it is a red pad, tom or not.");
    }

    [TestMethod]
    public void AMixTakesEffectFromTheFirstNoteAtOrAfterIt()
    {
        // Between the mixes, a note placed is flipped; from the end's tick on, it isn't.
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(0, Red), Pad(1920, Red));
        new AddDiscoFlip(ChartDifficulty.Expert, 500, 900).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);

        Assert.IsFalse(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 499));
        Assert.IsTrue(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 500));
        Assert.IsTrue(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 899));
        Assert.IsFalse(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 900));
    }

    [TestMethod]
    public void AFlipStillOnAtADifficultysLastNoteCarriesIntoTheNextOneUp()
    {
        // Easy's section ends after its last note, so the games never read the mix that ends it, and Medium starts flipped.
        var chart = Drums();
        Place(chart, ChartDifficulty.Easy, Pad(480, Red));
        Place(chart, ChartDifficulty.Medium, Pad(0, Red));
        new AddDiscoFlip(ChartDifficulty.Easy, 0, 1920).Apply(chart);
        var session = Of(chart, focused: DrumsMedium);

        Assert.AreEqual(Yellow, session.ShownLane(DrumsMedium, Pad(0, Red)));
        Assert.AreEqual(1, session.GameTrack(DrumsMedium).Notes.Single().Lane, "The game view plays it flipped too.");

        // Once Easy has a note at the section's end, its mix is read, and Medium isn't flipped.
        var ended = Drums();
        Place(ended, ChartDifficulty.Easy, Pad(480, Red), Pad(1920, Red));
        Place(ended, ChartDifficulty.Medium, Pad(0, Red));
        new AddDiscoFlip(ChartDifficulty.Easy, 0, 1920).Apply(ended);
        Assert.AreEqual(Red, Of(ended, focused: DrumsMedium).ShownLane(DrumsMedium, Pad(0, Red)));
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.FiveLane)]
    public void OnlyProDrumsAreFlipped(DrumsVariant variant)
    {
        var chart = Drums(variant);
        Place(chart, ChartDifficulty.Expert, Pad(480, Red), Pad(480, Yellow));
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);

        Assert.IsFalse(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 480));
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(480, Red)));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(480, Yellow)));
        Assert.AreEqual(Yellow, session.StoredLane(DrumsExpert, 480, Yellow));
    }

    [TestMethod]
    public void AChartWithoutDrumsHasNoFlip()
    {
        var session = Of(Guitar());

        Assert.IsFalse(session.DiscoFlips.IsFlipped(ChartDifficulty.Expert, 0));
        Assert.AreEqual(1, session.ShownLane(GuitarExpert, 0, 1));
    }

    [TestMethod]
    public void TheFlipsAndSectionsFollowEveryEditUndoAndRedo()
    {
        var chart = Drums();
        Place(chart, ChartDifficulty.Easy, Pad(480, Red));
        Place(chart, ChartDifficulty.Medium, Pad(0, Red));
        Place(chart, ChartDifficulty.Expert, Pad(480, Red));
        var session = Of(chart, focused: DrumsExpert);
        var easy = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Easy);
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(480, Red)));
        Assert.IsEmpty(session.DiscoFlipRangesOf(DrumsExpert));

        session.Tool = EditorTool.Phrase;
        session.PhraseKind = RailKind.DiscoFlip;
        DragFromTo(session, At(session, 0, Red, key: DrumsExpert), At(session, 960, Red, key: DrumsExpert));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(480, Red)), "Drawn.");
        Assert.HasCount(1, session.DiscoFlipRangesOf(DrumsExpert));

        session.Undo();
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(480, Red)), "Undone.");
        Assert.IsEmpty(session.DiscoFlipRangesOf(DrumsExpert));

        session.Redo();
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(480, Red)), "Redone.");
        Assert.HasCount(1, session.DiscoFlipRangesOf(DrumsExpert));

        session.Execute(new SetDrumsVariant(DrumsVariant.FourLane));
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(480, Red)), "4-lane drums aren't flipped.");
        session.Undo();
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(480, Red)), "Pro again.");

        // Easy's section ends after its last note, so Medium carries the flip, until a note on Easy is the first at or
        // after the mix that ends it.
        session.PhraseKind = RailKind.DiscoFlip;
        DragFromTo(session, At(session, 0, Red, key: easy), At(session, 1920, Red, key: easy));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsMedium, Pad(0, Red)));

        session.Tool = EditorTool.Place;
        Click(session, At(session, 1920, Blue, key: easy));
        Assert.AreEqual(Red, session.ShownLane(DrumsMedium, Pad(0, Red)), "Easy now reads the mix that ends its section.");
        session.Undo();
        Assert.AreEqual(Yellow, session.ShownLane(DrumsMedium, Pad(0, Red)));
    }

    // Editing notes inside a section.

    [TestMethod]
    public void CAndTheCymbalBoxLeaveRedAndYellowInsideASectionAlone()
    {
        // The games play them as a yellow cymbal and a red pad whatever the tom markers say.
        var session = Of(Flipped(Pad(480, Red), Pad(480, Yellow), Pad(480, Blue), Pad(2400, Yellow)), focused: DrumsExpert);
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;

        Assert.IsFalse(session.Tools.CanToggleCymbal(DrumsExpert, Pad(480, Red)));
        Assert.IsFalse(session.Tools.CanToggleCymbal(DrumsExpert, Pad(480, Yellow)));
        Assert.IsTrue(session.Tools.CanToggleCymbal(DrumsExpert, Pad(480, Blue)));
        Assert.IsTrue(session.Tools.CanToggleCymbal(DrumsExpert, Pad(2400, Yellow)), "Outside the section, yellow is yellow.");

        session.Selection.Select(DrumsExpert, [Pad(480, Red)]);
        session.Tools.ToggleCymbal();
        Assert.Contains("disco flip", session.Status!);
        session.Selection.Select(DrumsExpert, [Pad(480, Yellow)]);
        session.Tools.SetTom(true);
        Assert.Contains("disco flip", session.Status!);
        Assert.IsEmpty(drums.TomMarkers);
        Assert.AreEqual(0, session.UndoStack.UndoCount);

        // Among others, they're left out and the rest toggle.
        session.Selection.Select(DrumsExpert, Notes(session));
        session.Tools.ToggleCymbal();
        Assert.HasCount(2, drums.TomMarkers);
        Assert.IsTrue(DrumCymbals.IsTom(drums, Blue, 480) && DrumCymbals.IsTom(drums, Yellow, 2400));
        Assert.IsFalse(DrumCymbals.IsTom(drums, Yellow, 480));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AClickInAColumnInsideASectionHitsTheNoteDrawnThere(LaneView view)
    {
        var session = Of(Flipped(Pad(480, Red), Pad(2400, Red)), focused: DrumsExpert);

        Assert.AreEqual(Pad(480, Red), session.Tools.HitTest(At(session, 485, Yellow, view, DrumsExpert))?.Note);
        Assert.IsNull(session.Tools.HitTest(At(session, 485, Red, view, DrumsExpert)));
        Assert.AreEqual(Pad(2400, Red), session.Tools.HitTest(At(session, 2400, Red, view, DrumsExpert))?.Note, "Outside the section, red is red.");

        session.Tool = EditorTool.Erase;
        Click(session, At(session, 480, Yellow, view, DrumsExpert));
        Assert.AreSequenceEqual([Pad(2400, Red)], Notes(session));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void PlacingInsideASectionStoresThePadDrawnInTheColumnClicked(LaneView view)
    {
        var session = Of(Flipped(), focused: DrumsExpert);
        session.Tool = EditorTool.Place;

        Click(session, At(session, 965, Yellow, view, DrumsExpert));
        Click(session, At(session, 1440, Red, view, DrumsExpert));
        Click(session, At(session, 2400, Yellow, view, DrumsExpert));

        Assert.AreSequenceEqual([Pad(960, Red), Pad(1440, Yellow), Pad(2400, Yellow)], Notes(session));

        // Clicking a note drawn there again grabs it rather than placing another.
        Click(session, At(session, 960, Yellow, view, DrumsExpert));
        Assert.HasCount(3, Notes(session));
        Assert.AreSequenceEqual([Pad(960, Red)], session.Selection.Notes.ToList());
    }

    [TestMethod]
    public void TheNumberKeysInsideASectionPlaceAndTakeAwayThePadDrawnInTheirColumn()
    {
        var session = Of(Flipped(), focused: DrumsExpert);
        session.MoveCursorTo(960);

        session.Tools.ToggleNoteAtCursor(Yellow);
        Assert.AreSequenceEqual([Pad(960, Red)], Notes(session));

        session.Tools.ToggleNoteAtCursor(Red);
        Assert.AreSequenceEqual([Pad(960, Red), Pad(960, Yellow)], Notes(session));

        session.Tools.ToggleNoteAtCursor(Yellow);
        Assert.AreSequenceEqual([Pad(960, Yellow)], Notes(session), "2 again takes away the red drawn in yellow's column.");
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ABoxSelectsByTheColumnsTheNotesAreDrawnIn(LaneView view)
    {
        var session = Of(Flipped(Pad(480, Red), Pad(480, Yellow), Pad(480, Blue), Pad(2400, Yellow)), focused: DrumsExpert);

        DragFromTo(session, At(session, 300, Yellow, view, DrumsExpert), At(session, 2600, Yellow, view, DrumsExpert));

        Assert.AreSequenceEqual([Pad(480, Red), Pad(2400, Yellow)], session.Selection.Notes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingAcrossColumnsInsideASectionGivesThePadsDrawnWhereTheyLand(LaneView view)
    {
        // Red is drawn in yellow's column and yellow in red's: one column right, they're drawn in blue's and yellow's,
        // which are blue and red.
        var session = Of(Flipped(Pad(480, Red), Pad(480, Yellow)), focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, Notes(session));

        DragFromTo(session, At(session, 480, Yellow, view, DrumsExpert), At(session, 480, Blue, view, DrumsExpert));

        Assert.AreSequenceEqual([Pad(480, Red), Pad(480, Blue)], Notes(session));
        Assert.AreSequenceEqual([Pad(480, Red), Pad(480, Blue)], session.Selection.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsFalse(session.HasPreview);
        session.Undo();
        Assert.AreSequenceEqual([Pad(480, Red), Pad(480, Yellow)], Notes(session));
    }

    [TestMethod]
    public void AltArrowsMoveAcrossTheColumnsAsDrawn()
    {
        var session = Of(Flipped(Pad(480, Red), Pad(2400, Red)), focused: DrumsExpert);
        var slots = session.SlotsOf(ChartInstrument.Drums, LaneView.Highway);

        // Red drawn in yellow's column, moved left, is drawn in red's: it's yellow.
        session.Selection.Select(DrumsExpert, [Pad(480, Red)]);
        session.Tools.MoveSelection(0, -1, slots);
        Assert.AreSequenceEqual([Pad(480, Yellow), Pad(2400, Red)], Notes(session));

        // Outside the section, red can't go further left.
        session.Selection.Select(DrumsExpert, [Pad(2400, Red)]);
        session.Tools.MoveSelection(0, -1, slots);
        Assert.AreSequenceEqual([Pad(480, Yellow), Pad(2400, Red)], Notes(session));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AMoveAcrossColumnsAndOutOfASectionGivesThePadDrawnWhereItLands(LaneView view)
    {
        // Red drawn in yellow's column, dragged a column right and out of the section, is drawn in blue's: it's blue. Yellow
        // dragged from outside into it and a column left is drawn in red's: it stays yellow.
        var session = Of(Flipped(Pad(480, Red), Pad(2400, Yellow)), focused: DrumsExpert);

        DragFromTo(session, At(session, 480, Yellow, view, DrumsExpert), At(session, 2880, Blue, view, DrumsExpert));
        Assert.AreSequenceEqual([Pad(2400, Yellow), Pad(2880, Blue)], Notes(session));

        DragFromTo(session, At(session, 2400, Yellow, view, DrumsExpert), At(session, 960, Red, view, DrumsExpert));
        Assert.AreSequenceEqual([Pad(960, Yellow), Pad(2880, Blue)], Notes(session));
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AMoveInTimeIntoOrOutOfASectionKeepsTheStoredPads(LaneView view)
    {
        var session = Of(Flipped(Pad(480, Red), Pad(2400, Yellow)), focused: DrumsExpert);

        // Out: red drawn in yellow's column stays red, now drawn in red's.
        DragFromTo(session, At(session, 480, Yellow, view, DrumsExpert), At(session, 2880, Yellow, view, DrumsExpert));
        Assert.AreSequenceEqual([Pad(2400, Yellow), Pad(2880, Red)], Notes(session));
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(2880, Red)));

        // In: yellow stays yellow, now drawn in red's.
        DragFromTo(session, At(session, 2400, Yellow, view, DrumsExpert), At(session, 960, Yellow, view, DrumsExpert));
        Assert.AreSequenceEqual([Pad(960, Yellow), Pad(2880, Red)], Notes(session));
        Assert.AreEqual(Red, session.ShownLane(DrumsExpert, Pad(960, Yellow)));

        // In time with the keys, too: red, now drawn in yellow's column beside the yellow drawn in red's.
        session.Selection.Select(DrumsExpert, [Pad(2880, Red)]);
        session.Snap = SnapGrid.Of(2);
        session.Tools.MoveSelection(-2, 0, session.SlotsOf(ChartInstrument.Drums, view));
        Assert.AreSequenceEqual([Pad(960, Red), Pad(960, Yellow)], Notes(session));
        Assert.AreEqual(Yellow, session.ShownLane(DrumsExpert, Pad(960, Red)));
    }

    // The rail and the Phrase tool.

    [TestMethod]
    public void TheRailHasADiscoFlipRowOnEveryDrumsVariantButThePickerOffersItOnProOnly()
    {
        Assert.AreEqual(RailKind.DiscoFlip, PhraseRail.RowsOf(ChartInstrumentKind.Drums)[^1]);
        Assert.DoesNotContain(RailKind.DiscoFlip, PhraseRail.RowsOf(ChartInstrumentKind.FiveFret).ToList());
        Assert.DoesNotContain(RailKind.DiscoFlip, PhraseRail.RowsOf(ChartInstrumentKind.SixFret).ToList());

        Assert.Contains(RailKind.DiscoFlip, Of(Drums(), focused: DrumsExpert).ToolKindsOf(ChartInstrument.Drums).ToList());
        Assert.DoesNotContain(RailKind.DiscoFlip, Of(Drums(DrumsVariant.FourLane), focused: DrumsExpert).ToolKindsOf(ChartInstrument.Drums).ToList());
        Assert.DoesNotContain(RailKind.DiscoFlip, Of(Drums(DrumsVariant.FiveLane), focused: DrumsExpert).ToolKindsOf(ChartInstrument.Drums).ToList());
        Assert.DoesNotContain(RailKind.DiscoFlip, Of(Guitar()).ToolKindsOf(ChartInstrument.Guitar).ToList());
    }

    [TestMethod]
    public void DiscoFlipPickedGoesBackToStarPowerWhenTheDrumsStopBeingProOrTheFocusLeavesThem()
    {
        var chart = Drums();
        chart.AddPart(ChartInstrument.Guitar);
        var session = Of(chart, focused: DrumsExpert);

        session.PhraseKind = RailKind.DiscoFlip;
        session.Execute(new SetDrumsVariant(DrumsVariant.FourLane));
        Assert.AreEqual((RailKind)PhraseKind.StarPower, session.PhraseKind);

        session.Undo();
        session.PhraseKind = RailKind.DiscoFlip;
        session.Focused = GuitarExpert;
        Assert.AreEqual((RailKind)PhraseKind.StarPower, session.PhraseKind);
    }

    [TestMethod]
    public void ThePhraseToolDrawsASectionOnlyInProDrumsLanesAndAPhraseInAny()
    {
        var chart = Drums();
        chart.AddPart(ChartInstrument.Guitar);
        var session = Of(chart, focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;

        Assert.IsTrue(session.Tools.DrawsIn(DrumsExpert, RailKind.DiscoFlip));
        Assert.IsFalse(session.Tools.DrawsIn(GuitarExpert, RailKind.DiscoFlip), "Its start isn't shown there, as a press is refused.");
        Assert.IsTrue(session.Tools.DrawsIn(GuitarExpert, PhraseKind.StarPower));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ThePhraseToolDrawsASectionOnTheLanesDifficultyAsOneStep(LaneView view)
    {
        var session = Of(Drums(), focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = RailKind.DiscoFlip;

        DragFromTo(session, At(session, 950, Red, view, DrumsExpert), At(session, 130, Blue, view, DrumsExpert));

        var drawn = new DiscoFlipRange(120, 960, 0);
        Assert.AreSequenceEqual([drawn], Ranges(session, ChartDifficulty.Expert));
        Assert.IsEmpty(Ranges(session, ChartDifficulty.Hard));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, drawn)], session.Selection.DiscoFlips.ToList());

        // In the rail's row too, and a click adds nothing.
        DragFromTo(session, RailAt(session, 1920, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 2880, RailKind.DiscoFlip, view, DrumsExpert));
        Click(session, RailAt(session, 4000, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.AreSequenceEqual([drawn, new DiscoFlipRange(1920, 2880, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.IsFalse(session.Selection.HasDiscoFlips);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheSelectToolDrawsASectionInTheDiscoFlipRow(LaneView view)
    {
        var session = Of(Drums(), focused: DrumsExpert);
        session.Tool = EditorTool.Select;

        DragFromTo(session, RailAt(session, 0, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 960, RailKind.DiscoFlip, view, DrumsExpert));

        var drawn = new DiscoFlipRange(0, 960, 0);
        Assert.AreSequenceEqual([drawn], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, drawn)], session.Selection.DiscoFlips.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TakingBackASectionDrawnInTheRailLeavesItSelected(LaneView view)
    {
        var chart = Drums();
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        var section = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0));
        session.Selection.SelectDiscoFlips([section]);
        session.Tool = EditorTool.Select;

        session.Tools.Press(RailAt(session, 1920, RailKind.DiscoFlip, view, DrumsExpert));
        session.Tools.Drag(RailAt(session, 2880, RailKind.DiscoFlip, view, DrumsExpert));
        session.Tools.Cancel();

        Assert.AreSequenceEqual([new DiscoFlipRange(0, 960, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([section], session.Selection.DiscoFlips.ToList());
        Assert.IsFalse(session.HasPreview);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DrawingOverASectionIsRefusedAndLeavesNoPreview(LaneView view)
    {
        var chart = Drums();
        new AddDiscoFlip(ChartDifficulty.Expert, 960, 1920).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = RailKind.DiscoFlip;

        session.Tools.Press(At(session, 0, Red, view, DrumsExpert));
        session.Tools.Drag(At(session, 480, Red, view, DrumsExpert));
        Assert.IsTrue(session.HasPreview);
        session.Tools.Drag(At(session, 1440, Red, view, DrumsExpert));
        Assert.IsFalse(session.HasPreview);
        Assert.IsNotNull(session.Status);
        session.Tools.Release(At(session, 1440, Red, view, DrumsExpert));

        Assert.AreSequenceEqual([new DiscoFlipRange(960, 1920, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.FiveLane)]
    public void DrawingASectionOnDrumsThatArentProIsRefused(DrumsVariant variant)
    {
        var session = Of(Drums(variant), focused: DrumsExpert);
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = RailKind.DiscoFlip;

        DragFromTo(session, At(session, 0, Red, key: DrumsExpert), At(session, 960, Red, key: DrumsExpert));

        Assert.IsEmpty(Ranges(session, ChartDifficulty.Expert));
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheEraseToolErasesASectionInTheRail(LaneView view)
    {
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(480, Red));
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Tool = EditorTool.Erase;
        var point = RailAt(session, 480, RailKind.DiscoFlip, view, DrumsExpert);

        Assert.AreEqual(new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0)), session.Tools.EraseTargetAt(point)?.DiscoFlip);
        Click(session, point);

        Assert.IsEmpty(session.DiscoFlipRangesOf(DrumsExpert));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void PressingASectionsBarSelectsItAndShiftAddsOrTakesAway(LaneView view)
    {
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(480, Red));
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 960).Apply(chart);
        new AddDiscoFlip(ChartDifficulty.Expert, 1920, 2880).Apply(chart);
        new AddDiscoFlip(ChartDifficulty.Hard, 0, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        var hardKey = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard);
        var first = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0));
        var second = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(1920, 2880, 0));
        var hard = new DiscoFlipSection(ChartDifficulty.Hard, new DiscoFlipRange(0, 960, 0));
        session.Selection.Select(DrumsExpert, Notes(session));

        // Every tool but the erase tool selects in the rail.
        session.Tool = EditorTool.Place;
        Click(session, RailAt(session, 480, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.AreSequenceEqual([first], session.Selection.DiscoFlips.ToList());
        Assert.IsFalse(session.Selection.HasNotes, "Selecting a section drops the notes selected.");

        Click(session, RailAt(session, 2400, RailKind.DiscoFlip, view, DrumsExpert), EditorModifiers.Shift);
        Click(session, RailAt(session, 480, RailKind.DiscoFlip, view, hardKey), EditorModifiers.Shift);
        Assert.AreSequenceEqual([hard, first, second], session.Selection.DiscoFlips.ToList(), "Each lane's row is its own difficulty's.");

        Click(session, RailAt(session, 480, RailKind.DiscoFlip, view, DrumsExpert), EditorModifiers.Shift);
        Assert.AreSequenceEqual([hard, second], session.Selection.DiscoFlips.ToList());

        Click(session, RailAt(session, 4000, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.IsFalse(session.Selection.HasDiscoFlips);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void SelectingNotesOrTemposDropsTheSectionsUnlessAdding()
    {
        var chart = Drums();
        Place(chart, ChartDifficulty.Expert, Pad(480, Red));
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        var section = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0));
        var selection = session.Selection;

        selection.SelectDiscoFlips([section]);
        selection.Select(DrumsExpert, Notes(session));
        Assert.IsFalse(selection.HasDiscoFlips);

        selection.SelectDiscoFlips([section]);
        Assert.IsFalse(selection.HasNotes);
        selection.SelectTempos([0]);
        Assert.IsFalse(selection.HasDiscoFlips);

        selection.SelectDiscoFlips([section]);
        selection.Select(DrumsExpert, Notes(session), add: true);
        Assert.IsTrue(selection.HasNotes && selection.HasDiscoFlips, "Adding keeps both.");
        Assert.IsFalse(selection.IsEmpty);

        selection.SelectDiscoFlips([section]);
        Assert.AreEqual((0L, 960L), selection.Span, "A section selected alone spans its ticks.");
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingABarsBodyMovesTheSectionToTheGridAsOneStep(LaneView view)
    {
        var session = Of(WithSections((960, 1920)), focused: DrumsExpert);

        DragFromTo(session, RailAt(session, 1440, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 1700, RailKind.DiscoFlip, view, DrumsExpert));

        var moved = new DiscoFlipRange(1200, 2160, 0);
        Assert.AreSequenceEqual([moved], Ranges(session, ChartDifficulty.Expert));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, moved)], session.Selection.DiscoFlips.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingSeveralSelectedSectionsMovesThemTogether(LaneView view)
    {
        var session = Of(WithSections((0, 480), (960, 1440)), focused: DrumsExpert);
        session.Selection.SelectDiscoFlips(Sections(session, ChartDifficulty.Expert));

        DragFromTo(session, RailAt(session, 240, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 960, RailKind.DiscoFlip, view, DrumsExpert));

        Assert.AreSequenceEqual([new DiscoFlipRange(720, 1200, 0), new DiscoFlipRange(1680, 2160, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.HasCount(2, session.Selection.DiscoFlips);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingABarsEndsResizesTheSectionToTheGrid(LaneView view)
    {
        var session = Of(WithSections((960, 1920)), focused: DrumsExpert);

        DragFromTo(session, RailAt(session, 1915, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 2390, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.AreSequenceEqual([new DiscoFlipRange(960, 2400, 0)], Ranges(session, ChartDifficulty.Expert));

        DragFromTo(session, RailAt(session, 962, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 1450, RailKind.DiscoFlip, view, DrumsExpert));
        var resized = new DiscoFlipRange(1440, 2400, 0);
        Assert.AreSequenceEqual([resized], Ranges(session, ChartDifficulty.Expert));
        Assert.AreEqual(2, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, resized)], session.Selection.DiscoFlips.ToList());

        // Never past the other end: a grid step short of it.
        DragFromTo(session, RailAt(session, 2400, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 0, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.AreSequenceEqual([new DiscoFlipRange(1440, 1560, 0)], Ranges(session, ChartDifficulty.Expert));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ClickingABarsEndOrBodyOnlySelectsTheSection(LaneView view)
    {
        // Off the grid, as a loaded file can have it: a click mustn't snap it.
        var session = Of(WithSections((965, 1925)), focused: DrumsExpert);
        var range = new DiscoFlipRange(965, 1925, 0);

        Click(session, RailAt(session, 1925, RailKind.DiscoFlip, view, DrumsExpert));
        Click(session, RailAt(session, 968, RailKind.DiscoFlip, view, DrumsExpert));
        Click(session, RailAt(session, 1400, RailKind.DiscoFlip, view, DrumsExpert));
        DragFromTo(session, RailAt(session, 1400, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 1408, RailKind.DiscoFlip, view, DrumsExpert));

        Assert.AreSequenceEqual([range], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, range)], session.Selection.DiscoFlips.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsFalse(session.HasPreview);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AMoveOrResizeOntoAnotherSectionIsRefusedAndLeavesNoPreview(LaneView view)
    {
        // The second section's body, then its start, dragged a little later and then back over the first.
        foreach (double grabbed in (double[])[1200, 962])
        {
            var session = Of(WithSections((0, 480), (960, 1440)), focused: DrumsExpert);

            session.Tools.Press(RailAt(session, grabbed, RailKind.DiscoFlip, view, DrumsExpert));
            session.Tools.Drag(RailAt(session, grabbed + 240, RailKind.DiscoFlip, view, DrumsExpert));
            Assert.IsTrue(session.HasPreview);
            session.Tools.Drag(RailAt(session, grabbed - 960, RailKind.DiscoFlip, view, DrumsExpert));

            Assert.IsFalse(session.HasPreview, "A refused drag leaves the section as it was, not as the last drag made it.");
            Assert.IsNotNull(session.Status);
            session.Tools.Release(RailAt(session, grabbed - 960, RailKind.DiscoFlip, view, DrumsExpert));
            Assert.AreSequenceEqual([new DiscoFlipRange(0, 480, 0), new DiscoFlipRange(960, 1440, 0)], Ranges(session, ChartDifficulty.Expert));
            Assert.AreEqual(0, session.UndoStack.UndoCount);
            Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(960, 1440, 0))], session.Selection.DiscoFlips.ToList());
        }
    }

    [TestMethod]
    public void ASectionThatNeverEndsIsSelectedAndDeletedButNotMovedUntilItHasAnEnd()
    {
        var chart = Drums();
        chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Mixes.Add(new DrumMix(960, 0, DrumMix.DiscoFlipSetting));
        var session = Of(chart, focused: DrumsExpert);
        var open = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(960, null, 0));

        Assert.AreEqual(new DiscoFlipHit(open, PhraseGrip.Body), PhraseRail.HitTestDiscoFlip(Track(session), RailAt(session, 100_000, RailKind.DiscoFlip, key: DrumsExpert)));
        DragFromTo(session, RailAt(session, 1440, RailKind.DiscoFlip, key: DrumsExpert), RailAt(session, 2400, RailKind.DiscoFlip, key: DrumsExpert));
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([open], session.Selection.DiscoFlips.ToList());

        session.Tools.SetDiscoFlipStart(480);
        Assert.AreEqual(0, session.UndoStack.UndoCount);

        session.Tools.SetDiscoFlipEnd(1920);
        var ended = new DiscoFlipRange(960, 1920, 0);
        Assert.AreSequenceEqual([ended], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([new DiscoFlipSection(ChartDifficulty.Expert, ended)], session.Selection.DiscoFlips.ToList());

        session.Undo();
        session.Selection.SelectDiscoFlips([open]);
        session.Tools.DeleteSelection();
        Assert.IsEmpty(Ranges(session, ChartDifficulty.Expert));
    }

    [TestMethod]
    public void DeleteRemovesTheSelectedSectionsWithTheNotesAsOneStep()
    {
        var chart = WithSections((0, 960), (1920, 2880));
        new AddDiscoFlip(ChartDifficulty.Hard, 0, 960).Apply(chart);
        Place(chart, ChartDifficulty.Expert, Pad(480, Red));
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, Notes(session));
        session.Selection.SelectDiscoFlips(
            [
                new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0)),
                new DiscoFlipSection(ChartDifficulty.Hard, new DiscoFlipRange(0, 960, 0)),
            ],
            add: true);

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual([new DiscoFlipRange(1920, 2880, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.IsEmpty(Ranges(session, ChartDifficulty.Hard));
        Assert.IsEmpty(Notes(session));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Selection.IsEmpty);
        session.Undo();
        Assert.HasCount(2, Ranges(session, ChartDifficulty.Expert));
        Assert.HasCount(1, Ranges(session, ChartDifficulty.Hard));
        Assert.HasCount(1, Notes(session));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheSelectionDropsASectionUndoneAway(LaneView view)
    {
        var session = Of(WithSections((960, 1920)), focused: DrumsExpert);
        DragFromTo(session, RailAt(session, 1440, RailKind.DiscoFlip, view, DrumsExpert), RailAt(session, 1920, RailKind.DiscoFlip, view, DrumsExpert));
        Assert.IsTrue(session.Selection.HasDiscoFlips);

        session.Undo();
        Assert.IsFalse(session.Selection.HasDiscoFlips);

        // Redone, it's another section: the one selected went with the undo.
        session.Redo();
        Assert.IsFalse(session.Selection.HasDiscoFlips);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void EscapingAMoveLeavesTheSectionAndItsSelection(LaneView view)
    {
        var session = Of(WithSections((960, 1920)), focused: DrumsExpert);
        var section = new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(960, 1920, 0));

        session.Tools.Press(RailAt(session, 1440, RailKind.DiscoFlip, view, DrumsExpert));
        session.Tools.Drag(RailAt(session, 2400, RailKind.DiscoFlip, view, DrumsExpert));
        session.Tools.Cancel();

        Assert.AreSequenceEqual([section.Range], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([section], session.Selection.DiscoFlips.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // Properties.

    [TestMethod]
    public void SettingTheStartOrEndChangesEverySelectedSectionAsOneStep()
    {
        var chart = WithSections((0, 960));
        new AddDiscoFlip(ChartDifficulty.Hard, 240, 1680).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.SelectDiscoFlips(
            [
                new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(0, 960, 0)),
                new DiscoFlipSection(ChartDifficulty.Hard, new DiscoFlipRange(240, 1680, 0)),
            ]);

        session.Tools.SetDiscoFlipEnd(1440);
        Assert.AreSequenceEqual([new DiscoFlipRange(0, 1440, 0)], Ranges(session, ChartDifficulty.Expert));
        Assert.AreSequenceEqual([new DiscoFlipRange(240, 1440, 0)], Ranges(session, ChartDifficulty.Hard));
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Tools.SetDiscoFlipStart(480);
        Assert.AreSequenceEqual(
            [
                new DiscoFlipSection(ChartDifficulty.Hard, new DiscoFlipRange(480, 1440, 0)),
                new DiscoFlipSection(ChartDifficulty.Expert, new DiscoFlipRange(480, 1440, 0)),
            ],
            session.Selection.DiscoFlips.ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount);

        session.Tools.SetDiscoFlipStart(1440);
        Assert.IsNotNull(session.Status, "A start at the end is refused.");
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    // A Pro drums chart with the notes given on Expert and a disco flip section from 0 to 1920 there.
    private static EditableChart Flipped(params EditableNote[] notes)
    {
        var chart = WithSections((0, 1920));
        Place(chart, ChartDifficulty.Expert, notes);
        return chart;
    }

    // A Pro drums chart with the sections given on Expert.
    private static EditableChart WithSections(params (long Tick, long EndTick)[] sections)
    {
        var chart = Drums();
        foreach (var (tick, end) in sections)
            new AddDiscoFlip(ChartDifficulty.Expert, tick, end).Apply(chart);
        return chart;
    }

    private static void Place(EditableChart chart, ChartDifficulty difficulty, params EditableNote[] notes)
    {
        if (notes.Length > 0)
            new AddNotes(ChartInstrument.Drums, difficulty, notes).Apply(chart);
    }

    private static EditableNote Pad(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    private static EditableTrack Track(EditorSession session) => session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];

    private static List<EditableNote> Notes(EditorSession session) => [.. Track(session).Notes];

    private static List<DiscoFlipRange> Ranges(EditorSession session, ChartDifficulty difficulty) =>
        [.. DiscoFlips.RangesOf(session.Chart.PartOf(ChartInstrument.Drums)![difficulty])];

    private static List<DiscoFlipSection> Sections(EditorSession session, ChartDifficulty difficulty) =>
        [.. Ranges(session, difficulty).Select(r => new DiscoFlipSection(difficulty, r))];
}
