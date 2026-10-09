using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
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

    private static LaneProjection Project(EditorSession session, LaneView view, double width, double height)
    {
        var slots = session.SlotsOf(ChartInstrument.Guitar, view);
        return view == LaneView.Highway
            ? LaneProjection.Of(view, 0, height, 0, width / slots.Slots.Count, slots, session.Viewport, LookAhead)
            : LaneProjection.Of(view, 0, width, 0, height / slots.Slots.Count, slots, session.Viewport, LookAhead);
    }
}
