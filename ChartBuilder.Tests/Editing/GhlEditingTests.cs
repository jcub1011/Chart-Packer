using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// GHL's stacked columns (charter-spec C7 item 1): each of the three columns holds a white fret and a black one, the
/// black in the first half across the view, and the tools pick between them by the half the pointer is in, alike in
/// both views (AC C3.2).
/// </summary>
[TestClass]
public sealed class GhlEditingTests
{
    private const int White1 = 0, White2 = 1, White3 = 2, Black1 = 3, Black2 = 4, Black3 = 5;

    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    [TestMethod]
    public void TheBlackFretsShareTheWhiteFretsColumnsAndTheKeysPlaceWhiteThenBlack()
    {
        var highway = LaneSlots.Of(ChartInstrumentKind.SixFret, LaneView.Highway);
        var timeline = LaneSlots.Of(ChartInstrumentKind.SixFret, LaneView.Timeline);

        Assert.AreSequenceEqual([White1, White2, White3], highway.Slots.ToList());
        Assert.AreSequenceEqual([White1, White2, White3, ChartNote.OpenLane], timeline.Slots.ToList());
        Assert.AreEqual(1, highway.IndexOf(Black2));
        Assert.AreEqual(Black3, highway.PartnerOf(White3));
        Assert.AreEqual(White3, timeline.PartnerOf(Black3));
        Assert.IsNull(timeline.PartnerOf(ChartNote.OpenLane));
        Assert.AreSequenceEqual([White1, White2, White3, Black1, Black2, Black3], highway.KeyLanes.ToList());

        // Five-fret shares nothing.
        Assert.AreSequenceEqual([0, 1, 2, 3, 4], LaneSlots.Of(ChartInstrumentKind.FiveFret, LaneView.Highway).KeyLanes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheFirstHalfOfAColumnIsItsBlackFretAndTheSecondItsWhite(LaneView view)
    {
        var slots = LaneSlots.Of(ChartInstrumentKind.SixFret, view);
        var projection = new LaneProjection(view, 0, 600, 10, 60, slots, 0, 2);

        Assert.AreEqual(Black1, projection.LaneAt(10 + 20));
        Assert.AreEqual(White1, projection.LaneAt(10 + 40));
        Assert.AreEqual(Black2, projection.LaneAt(10 + 60 + 5));
        Assert.AreEqual(White3, projection.LaneAt(10 + 180 - 1));
        if (view == LaneView.Timeline)
            Assert.AreEqual(ChartNote.OpenLane, projection.LaneAt(10 + 180 + 10), "The open row isn't shared.");
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void PlacingInEachHalfOfAColumnMakesABarre(LaneView view)
    {
        var session = Of(Ghl(), focused: GhlExpert);
        session.Tool = EditorTool.Place;

        Click(session, At(session, 0, Black2, view, GhlExpert));
        Click(session, At(session, 0, White2, view, GhlExpert));

        Assert.AreSequenceEqual([new EditableNote(0, 1, White2), new EditableNote(0, 1, Black2)], Track(session).Notes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AClickPicksTheFretOfItsHalfAndALoneFretFromEither(LaneView view)
    {
        var session = Of(Ghl(), focused: GhlExpert);
        Track(session).Notes.Add(new EditableNote(0, 1, White1));
        Track(session).Notes.Add(new EditableNote(0, 1, Black1));
        Track(session).Notes.Add(new EditableNote(480, 1, Black3));

        Click(session, At(session, 0, Black1, view, GhlExpert));
        Assert.AreSequenceEqual([new NotePlace(0, Black1)], session.Selection.Places.ToList());

        Click(session, At(session, 0, White1, view, GhlExpert));
        Assert.AreSequenceEqual([new NotePlace(0, White1)], session.Selection.Places.ToList());

        // A black fret alone fills its column, so a click in the white half takes it.
        Click(session, At(session, 480, White3, view, GhlExpert));
        Assert.AreSequenceEqual([new NotePlace(480, Black3)], session.Selection.Places.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingIntoTheOtherHalfSwitchesTheFretAndAcrossMovesTheColumn(LaneView view)
    {
        var session = Of(Ghl(), focused: GhlExpert);
        Track(session).Notes.Add(new EditableNote(0, 1, Black1));

        DragFromTo(session, At(session, 0, Black1, view, GhlExpert), At(session, 0, White1, view, GhlExpert));
        Assert.AreSequenceEqual([new EditableNote(0, 1, White1)], Track(session).Notes.ToList());

        DragFromTo(session, At(session, 0, White1, view, GhlExpert), At(session, 0, Black2, view, GhlExpert));
        Assert.AreSequenceEqual([new EditableNote(0, 1, Black2)], Track(session).Notes.ToList());

        // Never out of the columns.
        DragFromTo(session, At(session, 0, Black2, view, GhlExpert), At(session, 0, ChartNote.OpenLane, view, GhlExpert));
        Assert.AreSequenceEqual([new EditableNote(0, 1, Black2)], Track(session).Notes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AWhiteAndABlackDraggedIntoTheOtherHalfOnlyMoveAcross(LaneView view)
    {
        var session = Of(Ghl(), focused: GhlExpert);
        Track(session).Notes.Add(new EditableNote(0, 1, White1));
        Track(session).Notes.Add(new EditableNote(480, 1, Black1));
        session.Selection.Select(GhlExpert, Track(session).Notes);

        DragFromTo(session, At(session, 0, White1, view, GhlExpert), At(session, 0, Black2, view, GhlExpert));

        Assert.AreSequenceEqual([new EditableNote(0, 1, White2), new EditableNote(480, 1, Black2)], Track(session).Notes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ABoxOverAColumnTakesBothItsFrets(LaneView view)
    {
        var session = Of(Ghl(), focused: GhlExpert);
        Track(session).Notes.Add(new EditableNote(480, 1, White2));
        Track(session).Notes.Add(new EditableNote(480, 1, Black2));
        Track(session).Notes.Add(new EditableNote(480, 1, White3));

        DragFromTo(session, At(session, 0, White2, view, GhlExpert), At(session, 960, White2, view, GhlExpert));

        Assert.AreSequenceEqual([new NotePlace(480, White2), new NotePlace(480, Black2)], session.Selection.Places.OrderBy(p => p.Lane).ToList());
    }

    [TestMethod]
    public void TheNumberKeysLanesPlaceEveryFret()
    {
        var session = Of(Ghl(), focused: GhlExpert);
        var keys = session.SlotsOf(ChartInstrument.GhlGuitar, LaneView.Highway).KeyLanes;

        foreach (int lane in keys)
            session.Tools.ToggleNoteAtCursor(lane);

        Assert.AreSequenceEqual([White1, White2, White3, Black1, Black2, Black3], Track(session).Notes.Select(n => n.Lane).ToList());
    }

    private static EditableTrack Track(EditorSession session) => session.Chart.PartOf(ChartInstrument.GhlGuitar)![ChartDifficulty.Expert];
}
