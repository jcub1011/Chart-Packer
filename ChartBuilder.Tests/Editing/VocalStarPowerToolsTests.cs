using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>A vocal part's star power (note 116) as the editor's tools draw, select, move, resize and remove it in the lane's rail.</summary>
[TestClass]
public sealed class VocalStarPowerToolsTests
{
    [TestMethod]
    public void ThePhraseToolDrawsStarPowerInTheRailAndSelectsIt()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, StarPowerRailAt(5), StarPowerRailAt(950));

        Assert.AreEqual(new VocalStarPowerEvent(0, 960), Vocals(session).StarPower.Single());
        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 960)], [.. session.Selection.VocalStarPower]);
        Assert.IsEmpty(Vocals(session).Phrases, "Star power isn't a lyric phrase.");
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DataRow(EditorTool.Select)]
    [DataRow(EditorTool.Place)]
    [DataRow(EditorTool.Phrase)]
    public void TheSelectPlaceAndPhraseToolsDrawStarPowerInAnEmptyPartOfTheRail(EditorTool tool)
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        session.Tool = tool;

        DragFromTo(session, StarPowerRailAt(5), StarPowerRailAt(950));

        Assert.AreEqual(new VocalStarPowerEvent(0, 960), Vocals(session).StarPower.Single());
        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 960)], [.. session.Selection.VocalStarPower]);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DataRow(EditorTool.Erase)]
    [DataRow(EditorTool.Tempo)]
    public void OtherToolsDrawNoStarPowerInTheRail(EditorTool tool)
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        session.Tool = tool;

        DragFromTo(session, StarPowerRailAt(5), StarPowerRailAt(950));

        Assert.IsEmpty(Vocals(session).StarPower);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ShiftLendsNoDrawingToTheRail()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));
        session.Tool = EditorTool.Select;

        DragFromTo(session, StarPowerRailAt(5), StarPowerRailAt(950), EditorModifiers.Shift);

        Assert.IsEmpty(Vocals(session).StarPower);
    }

    [TestMethod]
    public void ADragDrawsAcrossToThePressWhicheverWay()
    {
        var session = Of(WithVocals([]));
        session.Tool = EditorTool.Select;

        DragFromTo(session, StarPowerRailAt(1920), StarPowerRailAt(960));

        Assert.AreEqual(new VocalStarPowerEvent(960, 960), Vocals(session).StarPower.Single());
    }

    [TestMethod]
    public void TheHoverShowsWhereAPressWouldStartDrawingOnTheGrid()
    {
        var session = Of(WithVocals([], starPower: [new(960, 480)]));
        session.Tool = EditorTool.Select;

        Assert.AreEqual(session.Snap.Nearest(485, session.TempoMap), session.Tools.VocalStarPowerDrawStart(StarPowerRailAt(485)));
        Assert.IsNull(session.Tools.VocalStarPowerDrawStart(StarPowerRailAt(1000)), "A press on a bar grabs it.");
        Assert.IsNull(session.Tools.VocalStarPowerDrawStart(StarPowerRailAt(485), EditorModifiers.Shift), "Shift lends the press to the selection.");
        Assert.IsNull(session.Tools.VocalStarPowerDrawStart(VocalAt(485, 60)), "Only the rail draws star power.");
        Assert.IsNull(session.Tools.VocalStarPowerDrawStart(PhraseStripAt(485)));

        session.Tool = EditorTool.Erase;
        Assert.IsNull(session.Tools.VocalStarPowerDrawStart(StarPowerRailAt(485)), "The erase tool draws none.");
    }

    [TestMethod]
    public void StarPowerOverAPhraseIsAllowed()
    {
        var session = Of(WithVocals([], phrases: [new(0, 960)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, StarPowerRailAt(0), StarPowerRailAt(960));

        Assert.AreEqual(new VocalStarPowerEvent(0, 960), Vocals(session).StarPower.Single());
        Assert.AreEqual(new VocalPhraseEvent(0, 960), Vocals(session).Phrases.Single());
    }

    [TestMethod]
    public void StarPowerThatWouldOverlapAnotherIsRefused()
    {
        var session = Of(WithVocals([], starPower: [new(480, 480)]));
        session.Tool = EditorTool.Phrase;

        DragFromTo(session, StarPowerRailAt(0), StarPowerRailAt(720));

        Assert.AreEqual(new VocalStarPowerEvent(480, 480), Vocals(session).StarPower.Single());
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AStarPowerBarIsSelectedMovedAndResizedInTheRailWithAnyTool()
    {
        var session = Of(WithVocals([], starPower: [new(0, 960)]));
        session.Tool = EditorTool.Erase;

        DragFromTo(session, StarPowerRailAt(480), StarPowerRailAt(960));
        Assert.AreEqual(new VocalStarPowerEvent(480, 960), Vocals(session).StarPower.Single());

        DragFromTo(session, StarPowerRailAt(1440), StarPowerRailAt(1920));
        Assert.AreEqual(new VocalStarPowerEvent(480, 1440), Vocals(session).StarPower.Single());
        Assert.AreSequenceEqual([new VocalStarPowerEvent(480, 1440)], [.. session.Selection.VocalStarPower]);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AClickOnABarOnlySelectsIt()
    {
        var session = Of(WithVocals([], starPower: [new(0, 960)]));

        Click(session, StarPowerRailAt(480));

        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 960)], [.. session.Selection.VocalStarPower]);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ShiftAddsAndTakesAwayStarPowerFromTheSelection()
    {
        var session = Of(WithVocals([], starPower: [new(0, 480), new(960, 480)]));

        Click(session, StarPowerRailAt(240));
        Click(session, StarPowerRailAt(1200), EditorModifiers.Shift);
        Assert.HasCount(2, session.Selection.VocalStarPower);

        Click(session, StarPowerRailAt(240), EditorModifiers.Shift);
        Assert.AreSequenceEqual([new VocalStarPowerEvent(960, 480)], [.. session.Selection.VocalStarPower]);
    }

    [TestMethod]
    public void AClickOnNoBarSelectsNoStarPower()
    {
        var session = Of(WithVocals([], starPower: [new(0, 480)]));
        Click(session, StarPowerRailAt(240));

        Click(session, StarPowerRailAt(4800));

        Assert.IsEmpty(session.Selection.VocalStarPower);
    }

    [TestMethod]
    public void TheMiddleButtonRemovesTheStarPowerItPassesOver()
    {
        var session = Of(WithVocals([new(0, 100, 60)], starPower: [new(0, 480), new(960, 480)]));

        session.Tools.PressErase(StarPowerRailAt(200));
        session.Tools.Drag(StarPowerRailAt(700));
        session.Tools.Drag(StarPowerRailAt(1200));
        session.Tools.Release(StarPowerRailAt(1200));

        Assert.IsEmpty(Vocals(session).StarPower);
        Assert.HasCount(1, Vocals(session).Notes, "Only the rail is erased.");
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void DeleteRemovesTheSelectedStarPowerWithTheNotesAsOneStep()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 62)], [new(0, "a"), new(240, "b")], starPower: [new(0, 480), new(960, 480)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60)]);
        session.Selection.SelectVocalStarPower(VocalTrack.Vocals, [new(960, 480)], add: true);

        session.Tools.DeleteSelection();

        var content = Vocals(session);
        Assert.AreEqual(240, content.Notes.Single().Tick);
        Assert.AreEqual(new VocalStarPowerEvent(0, 480), content.StarPower.Single());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Selection.IsEmpty);
    }

    [TestMethod]
    public void TheEditIsUndoneAndRedone()
    {
        var session = Of(WithVocals([]));
        session.Tool = EditorTool.Phrase;
        DragFromTo(session, StarPowerRailAt(0), StarPowerRailAt(960));

        session.UndoStack.Undo();
        Assert.IsEmpty(Vocals(session).StarPower);

        session.UndoStack.Redo();
        Assert.AreEqual(new VocalStarPowerEvent(0, 960), Vocals(session).StarPower.Single());
    }

    [TestMethod]
    public void StarPowerIsPutOverTheSelectedNotesAndTakenAwayAgain()
    {
        var session = Of(WithVocals([new(0, 100, 60), new(240, 100, 62), new(1920, 100, 64)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60), new(240, 62)]);

        session.Tools.ToggleVocalStarPowerOverSelection();
        Assert.AreEqual(new VocalStarPowerEvent(0, 340), Vocals(session).StarPower.Single());

        session.Tools.ToggleVocalStarPowerOverSelection();
        Assert.IsEmpty(Vocals(session).StarPower);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void StarPowerOverNoSelectedNotesSaysSo()
    {
        var session = Of(WithVocals([new(0, 100, 60)]));

        session.Tools.ToggleVocalStarPowerOverSelection();

        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheStarPowerHitIsFoundWithItsGrip()
    {
        var session = Of(WithVocals([], starPower: [new(0, 960)]));

        var body = session.Tools.HitTestStarPower(StarPowerRailAt(480));
        var end = session.Tools.HitTestStarPower(StarPowerRailAt(955));

        Assert.AreEqual(new VocalStarPowerEvent(0, 960), body!.Value.StarPower);
        Assert.AreEqual(PhraseGrip.Body, body.Value.Grip);
        Assert.AreEqual(PhraseGrip.End, end!.Value.Grip);
        Assert.IsNull(session.Tools.HitTestStarPower(StarPowerRailAt(4800)));
    }

    [TestMethod]
    public void AStarPowerSelectionIsDroppedWhenTheChartNoLongerHasIt()
    {
        var session = Of(WithVocals([], starPower: [new(0, 960)]));
        Click(session, StarPowerRailAt(480));
        Assert.HasCount(1, session.Selection.VocalStarPower);

        session.Execute(new ChangeVocals(VocalTrack.Vocals, VocalEdits.RemoveStarPower(Vocals(session), [new VocalStarPowerEvent(0, 960)])));

        Assert.IsEmpty(session.Selection.VocalStarPower);
        Assert.IsTrue(session.Selection.IsEmpty);
    }
}
