using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class EditorViewportTests
{
    private const double LookAhead = 3;

    [TestMethod]
    public void SwitchingViewsKeepsTheSelectionTheCursorAndTheVisibleSpan()
    {
        // AC C3.1: the session holds the cursor and the selection, the lane its look-ahead, and each view only projects them.
        var chart = Guitar();
        chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.Add(new EditableNote(960, 1, 2));
        var session = Of(chart);
        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        session.Viewport.MoveCursor(5.25);
        var selected = session.Selection.Places.ToList();
        var (start, end) = session.Viewport.SpanOf(LookAhead);

        var highway = Project(session, LaneView.Highway, 200, 600);
        var timeline = Project(session, LaneView.Timeline, 900, 150);

        foreach (var view in new[] { highway, timeline })
        {
            Assert.AreEqual(start, view.Start, 1e-9);
            Assert.AreEqual(end, view.End, 1e-9);
            Assert.AreEqual(5.25, view.SecondsAt(view.StrikeAlong), 1e-9, "The strike line is on the cursor.");
        }

        // The strike line is as far through the lane in both, from the strike end.
        double highwayFraction = 1 - ((highway.StrikeAlong - highway.TimeFrom) / highway.TimeLength);
        double timelineFraction = (timeline.StrikeAlong - timeline.TimeFrom) / timeline.TimeLength;
        Assert.AreEqual(EditorViewport.StrikeFraction, highwayFraction, 1e-9);
        Assert.AreEqual(EditorViewport.StrikeFraction, timelineFraction, 1e-9);
        Assert.AreSequenceEqual(selected, [.. session.Selection.Places]);
    }

    [TestMethod]
    public void BothViewsMapAPixelToTheSameNoteLanesAndTime()
    {
        var session = Of(Guitar());
        var highway = Project(session, LaneView.Highway, 200, 600);
        var timeline = Project(session, LaneView.Timeline, 900, 150);

        // The highway's strike end is its bottom; the timeline's is its left.
        var (start, _) = session.Viewport.SpanOf(LookAhead);
        Assert.AreEqual(start, highway.SecondsAt(600), 1e-9);
        Assert.AreEqual(start, timeline.SecondsAt(0), 1e-9);
        Assert.AreEqual(2, highway.LaneAt(highway.AcrossOf(2)));
        Assert.AreEqual(2, timeline.LaneAt(timeline.AcrossOf(2)));
        Assert.AreEqual(ChartNote.OpenLane, timeline.LaneAt(timeline.AcrossOf(5)), "A timeline gives open notes a row.");
        Assert.IsNull(highway.LaneAt(-1));
        Assert.AreEqual(ChartNote.OpenLane, highway.Slots.Across, "A highway draws open notes across the lanes.");
    }

    [TestMethod]
    public void ScrollingMovesTheStrikeLineThroughTheSongWhereItIsOnScreen()
    {
        var session = Of(Guitar());
        int moved = 0;
        session.Viewport.Moved += (_, _) => moved++;
        var before = Project(session, LaneView.Highway, 200, 600);

        session.Viewport.ScrollBy(1.5);

        var after = Project(session, LaneView.Highway, 200, 600);
        Assert.AreEqual(1.5, session.Viewport.Cursor, 1e-9);
        Assert.AreEqual(before.StrikeAlong, after.StrikeAlong, 1e-9, "The strike line stays where it is in the lane.");
        Assert.AreEqual(1.5, after.SecondsAt(after.StrikeAlong), 1e-9);
        Assert.AreEqual(1, moved, "A scroll is the user's move, which playback seeks to.");
    }

    [TestMethod]
    public void FollowingThePlayheadMovesTheCursorWithoutSeeking()
    {
        var viewport = new EditorViewport();
        int moved = 0, changed = 0;
        viewport.Moved += (_, _) => moved++;
        viewport.Changed += (_, _) => changed++;

        viewport.Follow(10);

        Assert.AreEqual(10, viewport.Cursor);
        Assert.AreEqual(1, changed);
        Assert.AreEqual(0, moved);
    }

    [TestMethod]
    public void ALanesLookAheadIsKeptWithinRangeAndSnapped()
    {
        Assert.AreEqual(0.5, EditorViewport.ClampLookAhead(0), 1e-9, "Never below half a second.");
        Assert.AreEqual(60, EditorViewport.ClampLookAhead(100), 1e-9, "Never above 60 seconds.");
        Assert.AreEqual(1.3, EditorViewport.ClampLookAhead(1.26), 1e-9, "A tenth of a second below two seconds.");
        Assert.AreEqual(2.5, EditorViewport.ClampLookAhead(2.26), 1e-9, "Half a second up to ten.");
        Assert.AreEqual(2.0, EditorViewport.ClampLookAhead(2), 1e-9, "A value already on a step stays.");
        Assert.AreEqual(0.5, EditorViewport.ClampLookAhead(double.NaN), 1e-9, "Not a number is the shortest look-ahead.");
    }

    [TestMethod]
    public void ALanesLookAheadIsHowFarItShowsBeyondTheStrikeLine()
    {
        var viewport = new EditorViewport();
        viewport.MoveCursor(20);

        var (start, end) = viewport.SpanOf(2);

        Assert.AreEqual(22, end, 1e-9);
        Assert.AreEqual(EditorViewport.StrikeFraction, (20 - start) / (end - start), 1e-9);
        Assert.AreEqual(EditorViewport.MinLookAhead, EditorViewport.ClampLookAhead(0));
        Assert.AreEqual(EditorViewport.MaxLookAhead, EditorViewport.ClampLookAhead(1000));
    }

    [TestMethod]
    public void ASpanAheadIsShownAsItIsNotKeptToALookAheadsRange()
    {
        var viewport = new EditorViewport();
        viewport.MoveCursor(100);

        var (start, end) = viewport.SpanAhead(150);

        Assert.AreEqual(250, end, 1e-9, "More than the sixty seconds a look-ahead can be.");
        Assert.AreEqual(EditorViewport.StrikeFraction, (100 - start) / (end - start), 1e-9);
        Assert.AreEqual(100 + EditorViewport.MinLookAhead, viewport.SpanAhead(0).End, 1e-9, "Nothing to show is the shortest look-ahead.");
    }

    [TestMethod]
    public void InPixelsPerSecondALaneKeepsItsSecondsTheSameSizeWhateverItsHeight()
    {
        var session = Of(Guitar());
        session.Viewport.MoveCursor(10);
        var length = new HighwayLength(HighwayLengthMode.PixelsPerSecond, 2, 200);

        var shortLane = Project(session, LaneView.Highway, 200, 400, length);
        var tallLane = Project(session, LaneView.Highway, 200, 1200, length);

        Assert.AreEqual(200, shortLane.PixelsPerSecond, 1e-6);
        Assert.AreEqual(200, tallLane.PixelsPerSecond, 1e-6, "The same distance between notes.");
        Assert.IsGreaterThan(shortLane.SecondsOnScreen, tallLane.SecondsOnScreen, "A taller lane shows further ahead.");
        Assert.AreEqual(EditorViewport.StrikeFraction, (10 - tallLane.Start) / tallLane.SecondsOnScreen, 1e-9, "The strike line stays on the cursor.");
    }

    [TestMethod]
    public void InPixelsPerSecondATimelineIsSizedByItsWidth()
    {
        var session = Of(Guitar());
        var length = new HighwayLength(HighwayLengthMode.PixelsPerSecond, 2, 100);

        var narrow = Project(session, LaneView.Timeline, 500, 150, length);
        var wide = Project(session, LaneView.Timeline, 1500, 150, length);

        Assert.AreEqual(100, narrow.PixelsPerSecond, 1e-6);
        Assert.AreEqual(100, wide.PixelsPerSecond, 1e-6);
        Assert.AreEqual(narrow.SecondsOnScreen * 3, wide.SecondsOnScreen, 1e-6);
    }

    [TestMethod]
    public void InLookAheadALaneStretchesItsSecondsAcrossItsHeight()
    {
        var session = Of(Guitar());
        var length = new HighwayLength(HighwayLengthMode.LookAhead, LookAhead, 200);

        var shortLane = Project(session, LaneView.Highway, 200, 400, length);
        var tallLane = Project(session, LaneView.Highway, 200, 1200, length);

        Assert.AreEqual(shortLane.SecondsOnScreen, tallLane.SecondsOnScreen, 1e-9, "The same seconds, stretched.");
        Assert.AreEqual(3 * shortLane.PixelsPerSecond, tallLane.PixelsPerSecond, 1e-6);
    }

    private static LaneProjection Project(EditorSession session, LaneView view, double width, double height, HighwayLength length)
    {
        var slots = session.SlotsOf(ChartInstrument.Guitar, view);
        return view == LaneView.Highway
            ? LaneProjection.Of(view, 0, height, 0, width / slots.Slots.Count, slots, session.Viewport, length)
            : LaneProjection.Of(view, 0, width, 0, height / slots.Slots.Count, slots, session.Viewport, length);
    }

    private static LaneProjection Project(EditorSession session, LaneView view, double width, double height)
    {
        var slots = session.SlotsOf(ChartInstrument.Guitar, view);
        return view == LaneView.Highway
            ? LaneProjection.Of(view, 0, height, 0, width / slots.Slots.Count, slots, session.Viewport, LookAhead)
            : LaneProjection.Of(view, 0, width, 0, height / slots.Slots.Count, slots, session.Viewport, LookAhead);
    }
}
