using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charting;

[TestClass]
public sealed class LaneProjectionStrikeTests
{
    private static readonly LaneSlots Slots = LaneSlots.Of(ChartInstrumentKind.FiveFret, LaneView.Highway);

    [TestMethod]
    public void AHighwayWithItsStrikeLineNearItsBottomPutsTheCursorThereAndShowsTheLookAheadAboveIt()
    {
        // The note highway's: the strike line 87% of the way down, which is 13% of the way up from its strike end.
        var projection = LaneProjection.AtCursor(LaneView.Highway, 0, 1000, 0, 50, Slots, cursor: 20, strikeFraction: 0.13, secondsAhead: 2);

        Assert.AreEqual(870, projection.AlongOf(20), 1e-9, "The cursor is on the strike line.");
        Assert.AreEqual(870, projection.StrikeAlong, 1e-9);
        Assert.AreEqual(0, projection.AlongOf(22), 1e-9, "The look-ahead above it reaches the top.");
        Assert.AreEqual(22, projection.End, 1e-9);
        Assert.AreEqual(1000, projection.AlongOf(20 - (2 * 0.13 / 0.87)), 1e-9, "What's past the strike line shows until it falls off the bottom.");
        Assert.AreEqual(20, projection.SecondsAt(870), 1e-9);
    }

    [TestMethod]
    public void TheEditorsProjectionsKeepTheirQuarterStrikeLine()
    {
        var viewport = new EditorViewport();
        viewport.Follow(10);
        var projection = LaneProjection.Of(LaneView.Highway, 0, 800, 0, 50, Slots, viewport, 2);

        Assert.AreEqual(EditorViewport.StrikeFraction, projection.StrikeFraction, 1e-9);
        Assert.AreEqual(600, projection.StrikeAlong, 1e-9, "A quarter of the way up from the bottom.");
        Assert.AreEqual(600, projection.AlongOf(10), 1e-9);
    }

    [TestMethod]
    public void AHighwayInPerspectiveHasItsStrikeLineCloseToItsBottomWhateverItsLengthMode()
    {
        var viewport = new EditorViewport();
        viewport.Follow(10);

        foreach (var length in new[] { new HighwayLength(HighwayLengthMode.LookAhead, 2, 100), new HighwayLength(HighwayLengthMode.PixelsPerSecond, 2, 100) })
        {
            var projection = LaneProjection.Of(LaneView.Highway, 0, 3000, 0, 50, Slots, viewport, length, EditorViewport.PerspectiveStrikeFraction);

            Assert.AreEqual(EditorViewport.PerspectiveStrikeFraction, projection.StrikeFraction, 1e-9, $"{length.Mode}.");
            Assert.AreEqual(3000 * (1 - EditorViewport.PerspectiveStrikeFraction), projection.AlongOf(10), 1e-6, $"{length.Mode}: the cursor is on the strike line, near the bottom.");
            Assert.AreEqual(projection.StrikeAlong, projection.AlongOf(10), 1e-6, $"{length.Mode}.");
        }
    }

    [TestMethod]
    public void InPixelsPerSecondAPerspectiveHighwayKeepsItsSpeedAndShowsMoreAheadOfAStrikeLineNearItsBottom()
    {
        var viewport = new EditorViewport();
        viewport.Follow(10);
        var length = new HighwayLength(HighwayLengthMode.PixelsPerSecond, 2, 100);

        var quarter = LaneProjection.Of(LaneView.Highway, 0, 3000, 0, 50, Slots, viewport, length);
        var near = LaneProjection.Of(LaneView.Highway, 0, 3000, 0, 50, Slots, viewport, length, EditorViewport.PerspectiveStrikeFraction);

        Assert.AreEqual(100, quarter.PixelsPerSecond, 1e-6);
        Assert.AreEqual(100, near.PixelsPerSecond, 1e-6, "The notes keep their distance whichever way the strike line is placed.");
        Assert.IsGreaterThan(quarter.End - 10, near.End - 10, "More of the lane is ahead of a strike line near the bottom.");
    }

    [TestMethod]
    public void TheViewportsSpansForAStrikeFractionAgreeWithTheStaticOnes()
    {
        var viewport = new EditorViewport();
        viewport.Follow(10);

        Assert.AreEqual(EditorViewport.SpanAhead(10, 5, 0.05), viewport.SpanAhead(5, 0.05));
        Assert.AreEqual(EditorViewport.SpanAhead(10, EditorViewport.ClampLookAhead(5), 0.05), viewport.SpanOf(5, 0.05));
        Assert.AreEqual(viewport.SpanAhead(5), viewport.SpanAhead(5, EditorViewport.StrikeFraction));
    }

    [TestMethod]
    public void AWidenedProjectionKeepsItsStrikeFraction()
    {
        var projection = LaneProjection.AtCursor(LaneView.Highway, 0, 1000, 0, 50, Slots, 20, 0.13, 2).Widened(1, 3);

        Assert.AreEqual(0.13, projection.StrikeFraction, 1e-9);
        Assert.AreEqual(870, projection.AlongOf(20), 1e-6, "Every second is still drawn where it was.");
    }

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(-0.5)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void AStrikeFractionOutsideTheLaneIsKeptToOneItCanHaveSoTheSpanStaysFinite(double fraction)
    {
        var (start, end) = EditorViewport.SpanAhead(20, 2, fraction);
        var projection = LaneProjection.AtCursor(LaneView.Highway, 0, 1000, 0, 50, Slots, 20, fraction, 2);

        Assert.IsTrue(double.IsFinite(start) && double.IsFinite(end) && start <= 20 && end > 20, $"{start}..{end}");
        Assert.IsTrue(double.IsFinite(projection.AlongOf(20)));
        Assert.AreEqual(EditorViewport.ClampStrikeFraction(fraction), projection.StrikeFraction, 1e-9, "The projection keeps the fraction its span was made with.");
    }

    [TestMethod]
    public void AStrikeFractionWithinTheLaneIsLeftAsItIs()
    {
        Assert.AreEqual(0.87, EditorViewport.ClampStrikeFraction(0.87), 1e-9);
        Assert.AreEqual(EditorViewport.MaxStrikeFraction, EditorViewport.ClampStrikeFraction(1), 1e-9);
        Assert.AreEqual(0, EditorViewport.ClampStrikeFraction(-1), 1e-9);
        Assert.AreEqual(EditorViewport.StrikeFraction, EditorViewport.ClampStrikeFraction(double.NaN), 1e-9);
    }

    [TestMethod]
    public void AShortestLookAheadIsKeptWhateverIsAsked()
    {
        var projection = LaneProjection.AtCursor(LaneView.Highway, 0, 1000, 0, 50, Slots, 5, 0.13, secondsAhead: 0);

        Assert.IsTrue(projection.SecondsOnScreen > 0);
        Assert.AreEqual(870, projection.AlongOf(5), 1e-9);
    }
}
