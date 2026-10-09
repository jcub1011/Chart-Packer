using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// A lane's page: drawn once past the screen's edges and moved along the time axis as the song plays, so every second it
/// holds must stay where the screen draws it, and it must be drawn again once the view leaves it or the lane changes.
/// </summary>
[TestClass]
public sealed class LanePageTests
{
    private static readonly LaneSlots Slots = new([0, 1, 2, 3, 4], null);

    // 600 pixels for 4 seconds, from 10 s: 150 pixels a second.
    private static LaneProjection Screen(LaneView view, double start = 10) => new(view, 0, 600, 40, 30, Slots, start, 4);

    [TestMethod]
    public void WideningKeepsEverySecondWhereItIsDrawn()
    {
        foreach (var view in new[] { LaneView.Highway, LaneView.Timeline })
        {
            var screen = Screen(view);
            var page = screen.Widened(1, 3);

            Assert.AreEqual(9, page.Start, 1e-9, $"{view}'s start.");
            Assert.AreEqual(17, page.End, 1e-9, $"{view}'s end.");
            Assert.AreEqual(screen.PixelsPerSecond, page.PixelsPerSecond, 1e-9, $"{view}'s scale.");
            for (double seconds = 8; seconds <= 18; seconds += 0.37)
                Assert.AreEqual(screen.AlongOf(seconds), page.AlongOf(seconds), 1e-9, $"{view} at {seconds} s.");
        }
    }

    [TestMethod]
    public void AWidenedHighwayReachesUpForTheSecondsAheadAndDownForThoseBehind()
    {
        var page = Screen(LaneView.Highway).Widened(1, 3);

        // The start is at the bottom: 150 pixels below the screen; the end at the top, 450 pixels above it.
        Assert.AreEqual(-450, page.TimeFrom, 1e-9);
        Assert.AreEqual(1200, page.TimeLength, 1e-9);
        Assert.AreEqual(750, page.AlongOf(page.Start), 1e-9);
    }

    [TestMethod]
    public void AWidenedTimelineReachesLeftForTheSecondsBehindAndRightForThoseAhead()
    {
        var page = Screen(LaneView.Timeline).Widened(1, 3);

        Assert.AreEqual(-150, page.TimeFrom, 1e-9);
        Assert.AreEqual(1200, page.TimeLength, 1e-9);
        Assert.AreEqual(1050, page.AlongOf(page.End), 1e-9);
    }

    [TestMethod]
    public void APageServesTheViewUntilItLeavesThePage()
    {
        var page = LanePage.Of(Screen(LaneView.Highway), 0.25, 0.75);

        // A quarter of a screen back, three quarters on: from 9 s to 17 s, with 4 s on screen.
        Assert.IsTrue(page.Fits(Screen(LaneView.Highway)));
        Assert.IsTrue(page.Fits(Screen(LaneView.Highway, start: 9)));
        Assert.IsTrue(page.Fits(Screen(LaneView.Highway, start: 13)));
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway, start: 8.99)), "Before the page's start.");
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway, start: 13.01)), "Past the page's end.");
    }

    [TestMethod]
    public void APageDoesNotServeALaneLaidOutDifferently()
    {
        var screen = Screen(LaneView.Highway);
        var page = LanePage.Of(screen, 0.25, 0.75);

        Assert.IsFalse(page.Fits(screen with { TimeLength = 700 }), "Another size.");
        Assert.IsFalse(page.Fits(screen with { SecondsOnScreen = 5 }), "Another look-ahead.");
        Assert.IsFalse(page.Fits(screen with { LaneFrom = 50 }), "The columns moved.");
        Assert.IsFalse(page.Fits(screen with { LaneSize = 32 }), "The columns resized.");
        Assert.IsFalse(page.Fits(Screen(LaneView.Timeline)), "The other view.");
    }

    [TestMethod]
    public void APageNeedsItsMarginBeyondTheScreen()
    {
        var page = LanePage.Of(Screen(LaneView.Highway), 0.25, 0.75);

        // 30 pixels is 0.2 s at 150 pixels a second: the screen may reach no closer than that to the page's ends.
        Assert.IsTrue(page.Fits(Screen(LaneView.Highway, start: 9.2), 30));
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway, start: 9.19), 30));
        Assert.IsTrue(page.Fits(Screen(LaneView.Highway, start: 12.8), 30));
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway, start: 12.81), 30));
    }

    [TestMethod]
    public void APageServesColumnsMadeAgainTheSame()
    {
        var page = LanePage.Of(Screen(LaneView.Highway), 0.25, 0.75);

        Assert.IsTrue(page.Fits(Screen(LaneView.Highway) with { Slots = new LaneSlots([0, 1, 2, 3, 4], null) }));
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway) with { Slots = new LaneSlots([1, 2, 3, 4, 0], null) }), "Other lanes in the columns.");
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway) with { Slots = new LaneSlots([0, 1, 2, 3, 4], 7) }), "A lane drawn across them.");
        Assert.IsFalse(page.Fits(Screen(LaneView.Highway) with { Slots = new LaneSlots([0, 1, 2, 3, 4], null, [5, 6]) }), "Lanes sharing them.");
    }

    [TestMethod]
    public void APageTooLongIsCutBackAlikeBothWays()
    {
        // Two screens would be 1 200 pixels; 900 leaves 300 to reach past the screen, a quarter behind and three ahead.
        var page = LanePage.Of(Screen(LaneView.Timeline), 0.25, 0.75, maxLength: 900);

        Assert.AreEqual(900, page.Page.TimeLength, 1e-9);
        Assert.AreEqual(300, page.Reach, 1e-9);
        Assert.AreEqual(10 - 0.5, page.Page.Start, 1e-9);
        Assert.AreEqual(14 + 1.5, page.Page.End, 1e-9);

        // No longer than the screen, it reaches no further.
        Assert.AreEqual(0, LanePage.Of(Screen(LaneView.Timeline), 0.25, 0.75, maxLength: 500).Reach, 1e-9);
    }

    [TestMethod]
    public void ThePageMovesWithTheSongDownAHighwayAndLeftAlongATimeline()
    {
        var highway = LanePage.Of(Screen(LaneView.Highway), 0.25, 0.75);
        var timeline = LanePage.Of(Screen(LaneView.Timeline), 0.25, 0.75);

        Assert.AreEqual(0, highway.ShiftOf(Screen(LaneView.Highway)), 1e-9);
        Assert.AreEqual(75, highway.ShiftOf(Screen(LaneView.Highway, start: 10.5)), 1e-9, "Half a second later, 75 pixels further down.");
        Assert.AreEqual(-75, timeline.ShiftOf(Screen(LaneView.Timeline, start: 10.5)), 1e-9, "Half a second later, 75 pixels further left.");

        // Moved, the page draws every second where the screen now does.
        var now = Screen(LaneView.Highway, start: 11.2);
        double shift = highway.ShiftOf(now);
        foreach (double seconds in (double[])[11.2, 12, 14.9])
            Assert.AreEqual(now.AlongOf(seconds), highway.Page.AlongOf(seconds) + shift, 1e-9);
    }
}
