using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// A run of Generate fills in the editor (charter-spec C6 item 5): the proposals kept or dropped, by their ghosts in the
/// phrase rail or all at once, then added as one undoable step or cancelled, and a run ended by a change to the chart.
/// </summary>
[TestClass]
public sealed class FillGenerationEditingTests
{
    private const long Measure = 1920;
    private const int Kick = 0, Green = 4;

    [TestMethod]
    public void ARunProposesFillsEachKeptAndChangesNothing()
    {
        var session = Session();

        Assert.IsTrue(session.Fills.Start(FillOptions.Default, null));

        Assert.IsTrue(session.Fills.IsActive);
        Assert.HasCount(5, session.Fills.Proposals);
        Assert.AreEqual(5, session.Fills.KeptCount);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(Fills(session));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    public void TogglingKeepAllAndDropAllKeepOrDropTheProposals()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);

        session.Fills.Toggle(1);
        Assert.IsFalse(session.Fills.IsKept(1));
        Assert.AreEqual(4, session.Fills.KeptCount);
        session.Fills.Toggle(1);
        Assert.IsTrue(session.Fills.IsKept(1));

        session.Fills.DropAll();
        Assert.AreEqual(0, session.Fills.KeptCount);
        session.Fills.KeepAll();
        Assert.AreEqual(5, session.Fills.KeptCount);
    }

    [TestMethod]
    [DataRow(LaneView.Highway)]
    [DataRow(LaneView.Timeline)]
    public void ClickingAGhostInTheRailKeepsOrDropsItBeforeAnythingElse(LaneView view)
    {
        // With the Phrase tool drawing fills, a click on a ghost still only drops it.
        var session = Session();
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = PhraseKind.DrumFill;
        session.Fills.Start(FillOptions.Default, null);
        var second = session.Fills.Proposals[1].Fill;

        Click(session, RailAt(session, second.Tick + 500, PhraseKind.DrumFill, view, DrumsExpert));

        Assert.IsFalse(session.Fills.IsKept(1));
        Assert.AreEqual(4, session.Fills.KeptCount);
        Assert.AreEqual(0, session.UndoStack.UndoCount);

        Click(session, RailAt(session, second.Tick + 500, PhraseKind.DrumFill, view, DrumsExpert));
        Assert.IsTrue(session.Fills.IsKept(1));

        // Another row isn't a ghost's.
        Assert.IsNull(session.Fills.HitTest(RailAt(session, second.Tick + 500, PhraseKind.StarPower, view, DrumsExpert)));
    }

    [TestMethod]
    public void AcceptingAddsOnlyTheKeptFillsAsOneStepAndUndoRemovesThemAll()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);
        var proposals = session.Fills.Proposals.Select(p => p.Fill).ToList();
        session.Fills.Toggle(0);
        session.Fills.Toggle(3);

        Assert.IsTrue(session.Fills.Accept());

        Assert.AreSequenceEqual([proposals[1], proposals[2], proposals[4]], Fills(session).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsFalse(session.Fills.IsActive);
        Assert.IsNull(session.Status);

        session.Undo();
        Assert.IsEmpty(Fills(session));
        session.Redo();
        Assert.HasCount(3, Fills(session).ToList());
    }

    [TestMethod]
    public void AcceptingWithNoneKeptAddsNothingAndTheRunGoesOn()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);
        session.Fills.DropAll();

        Assert.IsFalse(session.Fills.Accept());

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsTrue(session.Fills.IsActive);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void CancellingAddsNothing()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);

        session.Fills.Cancel();

        Assert.IsFalse(session.Fills.IsActive);
        Assert.IsEmpty(session.Fills.Proposals);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(Fills(session));
    }

    [TestMethod]
    public void AChangeToTheChartEndsTheRunAndSaysSo()
    {
        // Said as the run ends, which the message bar shows, though the edit's own success clears the status after.
        var session = Session();
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);
        session.Fills.Start(FillOptions.Default, null);

        session.Execute(new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(Measure + 240, 1, Kick)]));

        Assert.IsFalse(session.Fills.IsActive);
        Assert.HasCount(1, said);
        Assert.Contains("dropped", said[0]);

        // An undo is a change too.
        session.Fills.Start(FillOptions.Default, null);
        session.Undo();
        Assert.IsFalse(session.Fills.IsActive);
    }

    [TestMethod]
    public void AChangeToTheSongsSettingsEndsTheRun()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);

        session.SetSongSettings([], TimeSpan.FromSeconds(1));

        Assert.IsFalse(session.Fills.IsActive);
        Assert.Contains("dropped", session.Status);
    }

    [TestMethod]
    public void AnAcceptTheChartRefusesAddsNothingSaysWhyAndTheRunGoesOn()
    {
        // [coda] slipped in under the run, not through the undo stack, so the run doesn't end: the fills after it are
        // refused as fills.
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);
        new AddTextEvent(null, TextEvent.Of(8 * Measure, "[coda]")).Apply(session.Chart);

        Assert.IsFalse(session.Fills.Accept());

        Assert.IsTrue(session.Fills.IsActive);
        Assert.AreEqual(5, session.Fills.KeptCount);
        Assert.HasCount(1, said);
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(Fills(session));
    }

    [TestMethod]
    public void UndoingAndRedoingAnAcceptSaysNothingAndStartsNoRun()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);
        session.Fills.Accept();
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);

        session.Undo();
        session.Redo();

        Assert.IsFalse(session.Fills.IsActive);
        Assert.IsEmpty(session.Fills.Proposals);
        Assert.IsEmpty(said);
        Assert.IsNull(session.Status);
        Assert.HasCount(5, Fills(session).ToList());
    }

    [TestMethod]
    public void AGesturesPreviewLeavesTheRunAlone()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);

        session.Preview(new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(Measure + 240, 1, Kick)]));
        session.Preview(null);

        Assert.IsTrue(session.Fills.IsActive);
    }

    [TestMethod]
    public void ARunWithFewerThanTwoStarPowerPhrasesProposesNothingAndSaysWhy()
    {
        var session = Session();
        new RemovePhrase(ChartInstrument.Drums, session.Chart.PartOf(ChartInstrument.Drums)!.Phrases[1]).Apply(session.Chart);

        Assert.IsFalse(session.Fills.Start(FillOptions.Default, null));

        Assert.IsFalse(session.Fills.IsActive);
        Assert.Contains("second star power phrase", session.Status);
    }

    [TestMethod]
    public void ARunOverTheSelectionsSpanProposesOnlyThere()
    {
        var session = Session();

        session.Fills.Start(FillOptions.Default, ((9 * Measure) + 1, 17 * Measure));

        Assert.AreSequenceEqual([10 * Measure, 14 * Measure], session.Fills.Proposals.Select(p => p.Fill.Tick).ToList());
    }

    [TestMethod]
    public void AnotherRunReplacesTheOneGoingOn()
    {
        var session = Session();
        session.Fills.Start(FillOptions.Default, null);
        session.Fills.DropAll();

        session.Fills.Start(new FillOptions(SpacingMeasures: 8), null);

        Assert.AreSequenceEqual([2 * Measure, 10 * Measure, 18 * Measure], session.Fills.Proposals.Select(p => p.Fill.Tick).ToList());
        Assert.AreEqual(3, session.Fills.KeptCount);
    }

    /// <summary>
    /// A session over Pro drums with star power over measures 0 and 1, and on Expert a green cymbal on the first beat of
    /// measures 0 to 20, with a kick between: five fills fit, at measures 2, 6, 10, 14 and 18.
    /// </summary>
    private static EditorSession Session()
    {
        var chart = Drums();
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        for (long m = 0; m <= 20; m++)
        {
            drums[ChartDifficulty.Expert].Notes.Add(new EditableNote(m * Measure, 1, Green));
            drums[ChartDifficulty.Expert].Notes.Add(new EditableNote((m * Measure) + 960, 1, Kick));
        }

        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 960));
        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, Measure, 960));
        return Of(chart, focused: DrumsExpert);
    }

    private static IEnumerable<Phrase> Fills(EditorSession session) =>
        session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.Where(static p => p.Kind == PhraseKind.DrumFill);
}
