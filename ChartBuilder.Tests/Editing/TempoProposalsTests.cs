using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Analysis;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;
using static ChartBuilder.Tests.Editing.ProposedTempoMapTests;

namespace ChartBuilder.Tests.Editing;

/// <summary>Looking at a proposed tempo map: adjusting it, doubling, halving, discarding (charter-spec C14 item 4).</summary>
[TestClass]
public sealed class TempoProposalsTests
{
    [TestMethod]
    public void DraggingABeatPinsItAndRespacesTheBeatsUpToTwoMeasuresAway()
    {
        var (session, proposals) = Started(Beats(120, 40, 1.0));

        Assert.IsTrue(proposals.MoveBeat(20, 11.1));

        // Two measures of 4/4 are eight beats: beats 12 and 28 stay put.
        Assert.AreEqual(11.1, proposals.Beats[20]);
        Assert.IsTrue(proposals.Pinned[20]);
        Assert.AreEqual(7.0, proposals.Beats[12], 1e-9, "Two measures before stays put.");
        Assert.AreEqual(15.0, proposals.Beats[28], 1e-9, "Two measures after stays put.");
        Assert.AreEqual(7.0 + (5 * 4.1 / 8), proposals.Beats[17], 1e-9, "The beats between are spaced evenly.");
        Assert.AreEqual(11.1 + (3.9 / 8), proposals.Beats[21], 1e-9);
        Assert.AreEqual(6.5, proposals.Beats[11], 1e-9, "Further away, nothing moves.");
        Assert.AreEqual(0, session.UndoStack.UndoCount, "Adjusting changes nothing in the chart.");
    }

    [TestMethod]
    public void APinnedBeatBoundsTheNextDrag()
    {
        var (_, proposals) = Started(Beats(120, 40, 1.0));
        proposals.MoveBeat(20, 11.1);

        proposals.MoveBeat(22, 12.2);

        Assert.AreEqual(11.1, proposals.Beats[20], "The pinned beat stays put.");
        Assert.AreEqual((11.1 + 12.2) / 2, proposals.Beats[21], 1e-9);
    }

    [TestMethod]
    public void ADragStopsShortOfTheBeatsThatStay()
    {
        var (_, proposals) = Started(Beats(120, 40, 1.0));
        proposals.MoveBeat(20, 11.0);

        proposals.MoveBeat(21, 5.0);

        Assert.AreEqual(11.0 + TempoProposals.MinGapSeconds, proposals.Beats[21], 1e-9);
        for (int i = 1; i < proposals.Beats.Count; i++)
            Assert.IsGreaterThan(proposals.Beats[i - 1], proposals.Beats[i]);
    }

    [TestMethod]
    public void DoublingAndHalvingKeepTheDownbeatAndPins()
    {
        var (_, proposals) = Started(Beats(60, 20, 1.0), downbeat: 2);
        proposals.MoveBeat(4, 5.1);

        proposals.Double();
        Assert.HasCount(39, proposals.Beats);
        Assert.AreEqual(4, proposals.Downbeat);
        Assert.IsTrue(proposals.Pinned[8]);
        Assert.AreEqual((proposals.Beats[0] + proposals.Beats[2]) / 2, proposals.Beats[1], 1e-9, "A beat between each two.");

        proposals.Halve();
        Assert.HasCount(20, proposals.Beats);
        Assert.AreEqual(2, proposals.Downbeat);
        Assert.IsTrue(proposals.Pinned[4]);
        Assert.AreEqual(5.1, proposals.Beats[4], 1e-9);
    }

    [TestMethod]
    public void TheRunGoesOnThroughEditsAndAStartingTempoChange()
    {
        var (session, proposals) = Started(Beats(120, 16, 1.0));

        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(480, 0, 1)]));
        session.Execute(new SetStartingTempo(400_000, null, null, keepTimes: false));
        session.SetSongSettings([KeyValuePair.Create("delay", "100")], TimeSpan.FromMilliseconds(100));

        Assert.IsTrue(proposals.IsActive);
        Assert.HasCount(16, proposals.Beats);
    }

    [TestMethod]
    public void DiscardingChangesNothing()
    {
        var (session, proposals) = Started(Beats(120, 16, 1.0));
        var before = session.Chart.Clone();

        proposals.Discard();

        Assert.IsFalse(proposals.IsActive);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(ChartShared.Core.Charting.ChartModelComparer.Differences(before, session.Chart));
    }

    [TestMethod]
    public void ResetTakesBackEveryAdjustment()
    {
        var (_, proposals) = Started(Beats(120, 16, 1.0), downbeat: 1);
        proposals.MoveBeat(5, 3.6);
        proposals.Double();
        proposals.ShiftDownbeat(3);

        proposals.Reset();

        Assert.AreSequenceEqual(Beats(120, 16, 1.0), proposals.Beats.ToList());
        Assert.AreEqual(1, proposals.Downbeat);
        Assert.IsTrue(proposals.Pinned.All(p => !p));
    }

    [TestMethod]
    public void TooFewBeatsStartsNothingAndSaysWhy()
    {
        var session = Of(Guitar());

        session.TempoProposal.Start(BeatProposal.None, 0);

        Assert.IsFalse(session.TempoProposal.IsActive);
        Assert.IsNotNull(session.Status);
    }

    private static (EditorSession Session, TempoProposals Proposals) Started(double[] beats, int downbeat = 0)
    {
        var session = Of(Guitar());
        session.TempoProposal.Start(Proposal(beats), downbeat);
        return (session, session.TempoProposal);
    }
}
