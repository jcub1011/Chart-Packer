using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// A Generate fills plan (charter-spec C6 item 5): the fills proposed, each kept or dropped, then added as one undoable
/// step, with the fills already there kept alongside or replaced.
/// </summary>
[TestClass]
public sealed class FillPlanTests
{
    private const long Measure = 1920;
    private const int Kick = 0, Green = 4;

    [TestMethod]
    public void APlanProposesFillsEachKeptAndChangesNothing()
    {
        var session = Session();

        var plan = Plan(session);

        Assert.IsTrue(plan.HasProposals);
        Assert.HasCount(5, plan.Regions);
        Assert.AreEqual(5, plan.KeptCount);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(Fills(session));
    }

    [TestMethod]
    public void TogglingKeepAllAndDropAllKeepOrDropTheProposals()
    {
        var session = Session();
        var plan = Plan(session);

        plan.Toggle(1);
        Assert.IsFalse(plan.Regions[1].Kept);
        Assert.AreEqual(4, plan.KeptCount);
        plan.Toggle(1);
        Assert.IsTrue(plan.Regions[1].Kept);

        plan.DropAll();
        Assert.AreEqual(0, plan.KeptCount);
        Assert.IsFalse(plan.AnyKept);
        plan.KeepAll();
        Assert.AreEqual(5, plan.KeptCount);
        Assert.IsTrue(plan.AnyKept);
    }

    [TestMethod]
    public void ApplyingAddsOnlyTheKeptFillsAsOneStepAndUndoRemovesThemAll()
    {
        var session = Session();
        var plan = Plan(session);
        var proposals = plan.Regions.Select(r => r.Phrase).ToList();
        plan.Toggle(0);
        plan.Toggle(3);

        Assert.IsTrue(session.Execute(plan.BuildCommand()!));

        Assert.AreSequenceEqual([proposals[1], proposals[2], proposals[4]], Fills(session).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.IsEmpty(Fills(session));
        session.Redo();
        Assert.HasCount(3, Fills(session).ToList());
    }

    [TestMethod]
    public void ApplyingWithNoneKeptHasNothingToDo()
    {
        var session = Session();
        var plan = Plan(session);
        plan.DropAll();

        Assert.IsFalse(plan.AnyKept);
        Assert.IsNull(plan.BuildCommand());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AddKeepsTheFillsAlreadyThereAndKeepsClearOfThem()
    {
        var session = Session();
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        var existing = new Phrase(PhraseKind.DrumFill, 4 * Measure, 1);
        drums.Phrases.Add(existing);

        var plan = Plan(session, GenerationMode.Add);

        Assert.HasCount(1, plan.Existing);
        Assert.HasCount(1, plan.SurvivingFills);
        Assert.IsTrue(plan.SurvivingFills.Contains(existing));
        Assert.IsFalse(plan.Regions.Any(r => r.Phrase.Tick == 4 * Measure), "A fill starts clear of the one there.");

        Assert.IsTrue(session.Execute(plan.BuildCommand()!));
        Assert.IsTrue(Fills(session).Contains(existing), "The fill already there stays.");
    }

    [TestMethod]
    public void ReplaceRemovesTheFillsWhollyInsideTheSpanAndAddsTheKeptOnes()
    {
        var session = Session();
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        var existing = new Phrase(PhraseKind.DrumFill, 4 * Measure, Measure);
        drums.Phrases.Add(existing);

        var plan = Plan(session, GenerationMode.Replace);

        Assert.HasCount(1, plan.Existing);
        Assert.IsEmpty(plan.SurvivingFills);

        Assert.IsTrue(session.Execute(plan.BuildCommand()!));
        Assert.IsFalse(Fills(session).Contains(existing), "The fill the plan replaced is gone.");
        Assert.HasCount(5, Fills(session).ToList());
    }

    [TestMethod]
    public void ReplaceOverASpanLeavesAFillReachingAcrossItsEdge()
    {
        var session = Session();
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        var crossing = new Phrase(PhraseKind.DrumFill, 9 * Measure, 2 * Measure);
        drums.Phrases.Add(crossing);

        var plan = Plan(session, GenerationMode.Replace, ((10 * Measure) + 1, 17 * Measure));

        Assert.IsEmpty(plan.Existing, "A fill reaching across the span's edge isn't replaced.");
        Assert.IsTrue(plan.SurvivingFills.Contains(crossing));
    }

    [TestMethod]
    public void APlanOverTheSelectionsSpanProposesOnlyThere()
    {
        var session = Session();

        var plan = Plan(session, span: ((9 * Measure) + 1, 17 * Measure));

        Assert.AreSequenceEqual([10 * Measure, 14 * Measure], plan.Regions.Select(r => r.Phrase.Tick).ToList());
    }

    [TestMethod]
    public void APlanWithFewerThanTwoStarPowerPhrasesProposesNothingAndSaysWhy()
    {
        var session = Session();
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        drums.Phrases.RemoveAt(drums.Phrases.Count - 1);

        var plan = Plan(session);

        Assert.IsFalse(plan.HasProposals);
        Assert.AreEqual(NoFillsReason.TooFewStarPower, plan.Reason);
    }

    [TestMethod]
    public void TheOptionsChangeWhereFillsAreProposed()
    {
        var session = Session();

        var plan = Plan(session, options: new FillOptions(SpacingMeasures: 8));

        Assert.AreSequenceEqual([2 * Measure, 10 * Measure, 18 * Measure], plan.Regions.Select(r => r.Phrase.Tick).ToList());
        Assert.AreEqual(3, plan.KeptCount);
    }

    private static FillPlan Plan(
        EditorSession session,
        GenerationMode mode = GenerationMode.Add,
        (long From, long To)? span = null,
        FillOptions? options = null) =>
        FillPlan.Generate(session.Chart, DrumsVariant.Pro, options ?? FillOptions.Default, mode, span);

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
