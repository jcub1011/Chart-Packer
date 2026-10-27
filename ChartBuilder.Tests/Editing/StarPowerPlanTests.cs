using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The star power layout the generator's window reviews (charter-spec C8 item 1): replacing or adding alongside the
/// star power already there, keeping and dropping regions, drawing and resizing them, and applying them as one step.
/// </summary>
[TestClass]
public sealed class StarPowerPlanTests
{
    private const long Measure = 1920;

    [TestMethod]
    public void ReplaceModeWorksTheLayoutOutAsIfTheStarPowerThereWereGone()
    {
        var withStarPower = Chart(starPower: true);
        var without = Chart(starPower: false);

        var replacing = StarPowerPlan.Generate(withStarPower, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, null);
        var plain = StarPowerPlan.Generate(without, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, null);

        Assert.HasCount(1, replacing.Existing);
        Assert.IsTrue(replacing.HasProposals);
        Assert.AreSequenceEqual(
            plain.Regions.Select(static r => r.Phrase).ToList(),
            replacing.Regions.Select(static r => r.Phrase).ToList());
    }

    [TestMethod]
    public void AddModeOverTheWholePartWithStarPowerIsRefusedButASpanIsNot()
    {
        var chart = Chart(starPower: true);

        var whole = StarPowerPlan.Generate(chart, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Add, null);
        var span = StarPowerPlan.Generate(chart, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Add, (30 * Measure, (56 * Measure) - 1));

        Assert.AreEqual(NoStarPowerReason.HasStarPower, whole.Reason);
        Assert.IsFalse(whole.HasProposals);
        Assert.IsTrue(span.HasProposals);
        Assert.IsTrue(span.Regions.All(r => r.Phrase.Tick >= 30 * Measure && r.Phrase.CoverEnd <= 56 * Measure));
        Assert.IsTrue(span.Regions.All(r => !r.Phrase.Overlaps(new Phrase(PhraseKind.StarPower, 20 * Measure, Measure))));
    }

    [TestMethod]
    public void RegionsAreKeptEveryRegionUntilDropped()
    {
        var plan = Plan();
        int count = plan.Regions.Count;

        Assert.AreEqual(count, plan.KeptCount);
        plan.Toggle(0);
        Assert.AreEqual(count - 1, plan.KeptCount);
        Assert.IsFalse(plan.Regions[0].Kept);
        plan.Toggle(0);
        Assert.IsTrue(plan.Regions[0].Kept);
        plan.DropAll();
        Assert.AreEqual(0, plan.KeptCount);
        plan.KeepAll();
        Assert.AreEqual(count, plan.KeptCount);
    }

    [TestMethod]
    public void ADrawnRegionIsAddedKeptAndPutInTimeOrder()
    {
        var plan = Plan();
        plan.DropAll();
        var phrase = new Phrase(PhraseKind.StarPower, 40 * Measure, Measure);

        int index = plan.Add(phrase);

        Assert.IsTrue(plan.Regions[index].Kept);
        Assert.IsTrue(plan.Regions[index].Added);
        Assert.AreEqual(phrase, plan.Regions[index].Phrase);
        Assert.AreEqual(-1, plan.Add(new Phrase(PhraseKind.StarPower, 40 * Measure, Measure)));
        Assert.AreEqual(1, plan.Regions.Count(r => r.Phrase == phrase));
    }

    [TestMethod]
    public void ARegionOverlappingAKeptOneCantBeDrawn()
    {
        var plan = Plan();
        var kept = plan.Regions[0].Phrase;
        var phrase = kept with { Tick = kept.Tick + 1 };

        Assert.IsTrue(plan.Validate(phrase).HasFlag(StarPowerRegionProblem.Overlap));
        Assert.AreEqual(-1, plan.Add(phrase));
    }

    [TestMethod]
    public void ARegionOutsideTheSpanCantBeDrawn()
    {
        var plan = StarPowerPlan.Generate(Chart(starPower: false), ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, (30 * Measure, (56 * Measure) - 1));

        Assert.IsTrue(plan.Validate(new Phrase(PhraseKind.StarPower, 10 * Measure, Measure)).HasFlag(StarPowerRegionProblem.OutsideScope));
        Assert.AreEqual(-1, plan.Add(new Phrase(PhraseKind.StarPower, 10 * Measure, Measure)));
    }

    [TestMethod]
    public void ADrawnRegionCanBeResizedAndRemoved()
    {
        var plan = Plan();
        plan.DropAll();
        int index = plan.Add(new Phrase(PhraseKind.StarPower, 40 * Measure, Measure));

        int moved = plan.Resize(index, 41 * Measure, 2 * Measure);

        Assert.AreEqual(41 * Measure, plan.Regions[moved].Phrase.Tick);
        Assert.AreEqual(2 * Measure, plan.Regions[moved].Phrase.Length);
        Assert.IsTrue(plan.Remove(moved));
        Assert.AreEqual(-1, plan.Regions.ToList().FindIndex(r => r.Phrase.Tick == 41 * Measure));
    }

    [TestMethod]
    public void ARegionCanBeResizedOnToAnotherOnlyWhenThatOneIsDropped()
    {
        var plan = Plan();
        var first = plan.Regions[0].Phrase;
        var second = plan.Regions[1].Phrase;

        Assert.AreEqual(-1, plan.Resize(0, first.Tick, second.Tick + second.Length - first.Tick));

        plan.SetKept(1, false);
        Assert.AreNotEqual(-1, plan.Resize(0, first.Tick, second.Tick + second.Length - first.Tick));
    }

    [TestMethod]
    public void ReplaceAppliesTheRemovalsAndTheKeptRegionsAsOneStep()
    {
        var chart = Chart(starPower: true);
        var session = Of(chart);
        var plan = StarPowerPlan.Generate(chart, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, null);
        plan.DropAll();
        plan.SetKept(0, true);
        plan.SetKept(1, true);
        var wanted = plan.Regions.Where(static r => r.Kept).Select(static r => r.Phrase).ToList();

        var command = plan.BuildCommand();
        Assert.IsInstanceOfType<CompositeCommand>(command);
        Assert.HasCount(plan.KeptCount + 1, ((CompositeCommand)command).Commands);
        Assert.IsTrue(session.Execute(command));

        var phrases = StarPower(session);
        Assert.HasCount(plan.KeptCount, phrases);
        Assert.AreSequenceEqual(wanted, phrases.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 20 * Measure, Measure)], StarPower(session).ToList());
    }

    [TestMethod]
    public void ReplaceLeavesStarPowerReachingAcrossTheSpanAndWontOverlapIt()
    {
        var chart = Chart(starPower: true);
        var straddler = new Phrase(PhraseKind.StarPower, (30 * Measure) - 480, 960);
        chart.PartOf(ChartInstrument.Guitar)!.Phrases.Add(straddler);

        var plan = StarPowerPlan.Generate(chart, ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, (30 * Measure, (56 * Measure) - 1));
        var command = plan.BuildCommand();

        Assert.IsFalse(plan.Existing.Contains(straddler));
        Assert.IsTrue(plan.SurvivingStarPower.Contains(straddler));
        Assert.IsTrue(plan.Regions.All(r => !r.Phrase.Overlaps(straddler)), "The proposals keep clear of the star power that stays.");
        var removes = command switch
        {
            CompositeCommand composite => composite.Commands.OfType<RemovePhrase>().ToList(),
            RemovePhrase remove => [remove],
            _ => [],
        };
        Assert.IsFalse(removes.Any(r => r.Phrase == straddler));
        Assert.IsTrue(plan.Validate(straddler with { Tick = straddler.Tick + 1 }).HasFlag(StarPowerRegionProblem.Overlap));
    }

    [TestMethod]
    public void CoverageSamplesTheMeterAndCountsThePassagesTheKeptStarPowerChargesFor()
    {
        var plan = Plan();
        var coverage = plan.Coverage(samples: 64);

        Assert.HasCount(64, coverage.Samples);
        Assert.HasCount(plan.Spikes.Count, coverage.Spikes);
        Assert.IsTrue(coverage.Samples.Any(static s => s.Meter > 0));
        Assert.IsTrue(coverage.Samples.All(static s => s.Meter is >= 0 and <= 1));
    }

    [TestMethod]
    public void CoverageWithNothingKeptChargesNothing()
    {
        var plan = Plan();
        plan.DropAll();

        var coverage = plan.Coverage(samples: 32);

        Assert.IsTrue(coverage.Samples.All(static s => s.Meter == 0 && !s.Active));
        Assert.IsTrue(coverage.Spikes.All(static s => !s.Charged));
    }

    private static StarPowerPlan Plan() =>
        StarPowerPlan.Generate(Chart(starPower: false), ChartInstrument.Guitar, StarPowerOptions.Default, StarPowerMode.Replace, null);

    /// <summary>
    /// A chart of quarter notes on Expert for 64 measures (128 s), with star power at measure 20 when
    /// <paramref name="starPower"/> is true: six phrases fit.
    /// </summary>
    private static EditableChart Chart(bool starPower)
    {
        var chart = Guitar();
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < 4; q++)
                expert.Notes.Add(new EditableNote((m * Measure) + (q * 480), 1, q % 3));
        }

        if (starPower)
            chart.PartOf(ChartInstrument.Guitar)!.Phrases.Add(new Phrase(PhraseKind.StarPower, 20 * Measure, Measure));
        return chart;
    }

    private static IEnumerable<Phrase> StarPower(EditorSession session) =>
        session.Chart.PartOf(ChartInstrument.Guitar)!.Phrases.Where(static p => p.Kind == PhraseKind.StarPower);
}
