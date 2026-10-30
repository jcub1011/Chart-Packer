using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>What the Edit menu's items are enabled for: what the canvas's keys would act on.</summary>
[TestClass]
public sealed class EditMenuStateTests
{
    private static readonly ChartClip HighwayClip = new(480, 0, null, [new ClipTrack(GuitarExpert, ChartInstrumentKind.FiveFret, [], [])]);

    private static readonly ChartClip VocalClip = new(480, 0, null, []) { Vocals = new VocalClip(VocalTrack.Harmony1, []) };

    [TestMethod]
    public void NothingSelectedLeavesTheSelectionItemsOff()
    {
        var session = Session();

        Assert.IsFalse(EditMenuState.CanCopy(session.Selection));
        Assert.IsFalse(EditMenuState.CanDelete(session.Selection));
        Assert.IsFalse(EditMenuState.CanQuantize(session.Selection));
    }

    [TestMethod]
    public void SelectedNotesTurnTheSelectionItemsOn()
    {
        var session = Session();
        session.Selection.Select(GuitarExpert, session.TrackOf(GuitarExpert)!.Notes);

        Assert.IsTrue(EditMenuState.CanCopy(session.Selection));
        Assert.IsTrue(EditMenuState.CanDelete(session.Selection));
        Assert.IsTrue(EditMenuState.CanQuantize(session.Selection));
    }

    [TestMethod]
    public void ARangeOfNoLanesIsNothingToDelete()
    {
        var session = Session();
        session.Selection.SelectRange(0, 960, []);

        Assert.IsFalse(session.Selection.IsEmpty);
        Assert.IsFalse(EditMenuState.CanDelete(session.Selection));
    }

    [TestMethod]
    public void SelectedVocalNotesCanBeCopiedAndDeletedButNotQuantized()
    {
        var session = Of(WithVocals([new VocalNoteEvent(0, 120, 60, 100)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, Vocals(session).Notes.Select(static n => n.Place).ToList());

        Assert.IsTrue(EditMenuState.CanCopy(session.Selection));
        Assert.IsTrue(EditMenuState.CanDelete(session.Selection));
        Assert.IsFalse(EditMenuState.CanQuantize(session.Selection));
    }

    [TestMethod]
    public void SelectAllNeedsAFocusedLane()
    {
        var session = Session();
        Assert.IsTrue(EditMenuState.CanSelectAll(session));

        session.Focused = null;
        Assert.IsFalse(EditMenuState.CanSelectAll(session));
    }

    [TestMethod]
    public void SelectAllInAFocusedVocalLaneNeedsNoSelectedNotes()
    {
        var session = Of(WithVocals([new VocalNoteEvent(0, 120, 60, 100)]));
        session.Focused = null;
        Assert.IsFalse(EditMenuState.CanSelectAll(session));
        Assert.IsNull(EditMenuState.SelectAllVocalTarget(session));

        session.FocusedVocal = VocalTrack.Vocals;

        Assert.IsTrue(EditMenuState.CanSelectAll(session));
        Assert.AreEqual(VocalTrack.Vocals, EditMenuState.SelectAllVocalTarget(session));
    }

    [TestMethod]
    public void PasteNeedsAClipAndALane()
    {
        var session = Session();
        var clipboard = new ChartClipboard();
        Assert.IsFalse(EditMenuState.CanPaste(session, clipboard));

        clipboard.Current = HighwayClip;
        Assert.IsTrue(EditMenuState.CanPaste(session, clipboard));

        session.Focused = null;
        Assert.IsFalse(EditMenuState.CanPaste(session, clipboard));
    }

    [TestMethod]
    public void AClipOfVocalNotesPastesWithoutAFocusedHighway()
    {
        var session = Of(WithVocals([], track: VocalTrack.Harmony1));
        session.Focused = null;
        var clipboard = new ChartClipboard { Current = VocalClip };

        Assert.IsTrue(EditMenuState.CanPaste(session, clipboard));
    }

    [TestMethod]
    public void AClipOfVocalNotesIsRefusedWhereTheChartHasNoSuchPart()
    {
        var session = Session();
        var clipboard = new ChartClipboard { Current = VocalClip };

        Assert.IsFalse(EditMenuState.CanPaste(session, clipboard));
    }

    [TestMethod]
    public void AHighwayClipDoesNotPasteIntoAVocalLane()
    {
        var session = Of(WithVocals([]));
        var clipboard = new ChartClipboard { Current = HighwayClip };

        Assert.IsTrue(EditMenuState.CanPaste(session, clipboard));
        Assert.IsFalse(EditMenuState.CanPaste(session, clipboard, VocalTrack.Vocals));
    }

    [TestMethod]
    public void AVocalClipPastesBackIntoThePartItCameFromWhenNothingIsFocusedOrSelected()
    {
        var session = Session();

        Assert.AreEqual(VocalTrack.Harmony1, EditMenuState.PasteVocalTarget(session, VocalClip));
        Assert.IsNull(EditMenuState.PasteVocalTarget(session, HighwayClip));
    }

    [TestMethod]
    public void AVocalClipPastesIntoTheFocusedVocalLaneBeforeThePartItCameFrom()
    {
        var session = Session();
        session.FocusedVocal = VocalTrack.Harmony2;

        Assert.AreEqual(VocalTrack.Harmony2, EditMenuState.PasteVocalTarget(session, VocalClip));
        Assert.IsNull(EditMenuState.PasteVocalTarget(session, HighwayClip));
    }

    [TestMethod]
    public void ALanesOwnVocalPartIsWhereItsMenuPastes()
    {
        var session = Session();
        session.FocusedVocal = VocalTrack.Harmony2;

        Assert.AreEqual(VocalTrack.Vocals, EditMenuState.PasteTarget(session, VocalClip, VocalTrack.Vocals));
        Assert.AreEqual(VocalTrack.Harmony2, EditMenuState.PasteTarget(session, VocalClip, null));
        Assert.IsNull(EditMenuState.PasteTarget(session, HighwayClip, null));
    }

    [TestMethod]
    public void TheClipboardSaysWhenItChanges()
    {
        var clipboard = new ChartClipboard();
        int changes = 0;
        clipboard.Changed += (_, _) => changes++;

        clipboard.Current = HighwayClip;

        Assert.AreEqual(1, changes);
    }

    private static EditorSession Session()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 1)]).Apply(chart);
        return Of(chart);
    }
}
