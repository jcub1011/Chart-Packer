using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Going from one ghost of a run of Generate fills or Generate star power to the next or the one before (charter-spec C8
/// item 1): the run bar's Previous and Next.
/// </summary>
[TestClass]
public sealed class ProposalNavigationTests
{
    private const long Measure = 1920;

    [TestMethod]
    public void NextIsTheFirstProposalStartingAfterTheTick()
    {
        var run = Run();
        var starts = run.Phrases.Select(static p => p.Tick).ToList();

        Assert.AreEqual(0, run.NextAfter(-1));
        Assert.AreEqual(1, run.NextAfter(starts[0]), "One starting at the tick isn't after it.");
        Assert.AreEqual(1, run.NextAfter(starts[0] + 1));
        Assert.IsNull(run.NextAfter(starts[^1]));
    }

    [TestMethod]
    public void PreviousIsTheLastProposalStartingBeforeTheTick()
    {
        var run = Run();
        var starts = run.Phrases.Select(static p => p.Tick).ToList();

        Assert.AreEqual(starts.Count - 1, run.PreviousBefore(long.MaxValue));
        Assert.AreEqual(0, run.PreviousBefore(starts[1]), "One starting at the tick isn't before it.");
        Assert.AreEqual(1, run.PreviousBefore(starts[1] + 1));
        Assert.IsNull(run.PreviousBefore(starts[0]));
    }

    [TestMethod]
    public void WithoutARunThereIsNothingToGoTo()
    {
        var session = Of(Guitar());

        Assert.IsNull(session.StarPower.NextAfter(0));
        Assert.IsNull(session.StarPower.PreviousBefore(long.MaxValue));
    }

    // A run of Generate star power over 64 measures of quarter notes: several proposals.
    private static StarPowerProposals Run()
    {
        var chart = Guitar();
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < 4; q++)
                expert.Notes.Add(new EditableNote((m * Measure) + (q * 480), 1, q % 3));
        }

        var session = Of(chart);
        Assert.IsTrue(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null));
        Assert.IsGreaterThan(2, session.StarPower.Phrases.Count);
        return session.StarPower;
    }
}
