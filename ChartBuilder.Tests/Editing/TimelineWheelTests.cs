using ChartBuilder.Controls;

namespace ChartBuilder.Tests.Editing;

/// <summary>Wheel scrolling over the editor's timeline: the playhead moves from bar line to bar line.</summary>
[TestClass]
public sealed class TimelineWheelTests
{
    // Bars at 1, 3, 5 and 7 seconds, two beats each.
    private static readonly TimelineBeat[] Beats =
    [
        new(1, true), new(2, false),
        new(3, true), new(4, false),
        new(5, true), new(6, false),
        new(7, true), new(8, false),
    ];

    [TestMethod]
    public void OnABarLineOneNotchMovesOneBarEachWay()
    {
        Assert.AreEqual(5.0, TimelineView.StepBars(Beats, 3, 1));
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 3, -1));
    }

    [TestMethod]
    public void PartwayThroughABarBackGoesToItsStart()
    {
        Assert.AreEqual(5.0, TimelineView.StepBars(Beats, 4, 1), "Beat lines aren't stopped at.");
        Assert.AreEqual(3.0, TimelineView.StepBars(Beats, 4, -1));
        Assert.AreEqual(3.0, TimelineView.StepBars(Beats, 3.5, -1));
    }

    [TestMethod]
    public void JustOffABarLineCountsAsOnIt()
    {
        Assert.AreEqual(5.0, TimelineView.StepBars(Beats, 3.0004, 1));
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 3.0004, -1));
        Assert.AreEqual(5.0, TimelineView.StepBars(Beats, 2.9996, 1));
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 2.9996, -1));
    }

    [TestMethod]
    public void SeveralNotchesMoveSeveralBars()
    {
        Assert.AreEqual(7.0, TimelineView.StepBars(Beats, 1, 3));
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 7, -3));
        Assert.AreEqual(5.0, TimelineView.StepBars(Beats, 2, 2));
    }

    [TestMethod]
    public void PastTheEndsStopsAtTheLastBarLine()
    {
        Assert.AreEqual(7.0, TimelineView.StepBars(Beats, 3, 4));
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 5, -4));
    }

    [TestMethod]
    public void NoBarLineThatWayMovesNothing()
    {
        Assert.IsNull(TimelineView.StepBars(Beats, 7, 1));
        Assert.IsNull(TimelineView.StepBars(Beats, 8, 1));
        Assert.IsNull(TimelineView.StepBars(Beats, 1, -1));
        Assert.IsNull(TimelineView.StepBars(Beats, 0, -1));
        Assert.IsNull(TimelineView.StepBars([], 3, 1));
        Assert.IsNull(TimelineView.StepBars([new(1, false), new(2, false)], 0, 1));
        Assert.IsNull(TimelineView.StepBars(Beats, 3, 0));
    }

    [TestMethod]
    public void BeforeTheFirstBarForwardGoesToIt()
    {
        Assert.AreEqual(1.0, TimelineView.StepBars(Beats, 0, 1));
    }
}
