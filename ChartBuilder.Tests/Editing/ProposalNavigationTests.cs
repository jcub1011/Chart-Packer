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
        Assert.IsFalse(session.StarPower.GoToNext());
        Assert.IsFalse(session.StarPower.GoToPrevious());
    }

    [TestMethod]
    public void NextAndPreviousReachEveryProposalWhateverTheSnap()
    {
        // In 3/4 a whole-note grid misses most measure lines, where the proposals start, so the snapped cursor would sit
        // before or after the one it was moved to.
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 3, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        var session = Of(chart);
        var run = Start(session, 3);
        session.Snap = SnapGrid.Of(1);
        var starts = run.Phrases.Select(static p => p.Tick).ToList();
        Assert.Contains(t => t % (4 * 480) != 0, starts, "Some proposal is off the grid.");

        // Bounded, so one found again fails the test rather than going on forever.
        session.MoveCursorTo(0);
        var forward = new List<long>();
        while (forward.Count <= starts.Count && run.GoToNext())
            forward.Add(CursorAt(session));
        var back = new List<long>();
        while (back.Count <= starts.Count && run.GoToPrevious())
            back.Add(CursorAt(session));

        Assert.AreSequenceEqual(starts, forward);
        Assert.AreSequenceEqual(starts.AsEnumerable().Reverse().Skip(1).ToList(), back);
    }

    // A run of Generate star power over 64 measures of quarter notes: several proposals.
    private static StarPowerProposals Run() => Start(Of(Guitar()), 4);

    private static StarPowerProposals Start(EditorSession session, int beatsPerMeasure)
    {
        var expert = session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < beatsPerMeasure; q++)
                expert.Notes.Add(new EditableNote((m * beatsPerMeasure * 480) + (q * 480), 1, q % 3));
        }

        Assert.IsTrue(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null));
        Assert.IsGreaterThan(2, session.StarPower.Phrases.Count);
        return session.StarPower;
    }

    private static long CursorAt(EditorSession session) => (long)Math.Round(session.TickAt(session.Viewport.Cursor));
}
