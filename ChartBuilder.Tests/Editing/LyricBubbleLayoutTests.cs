using Avalonia;
using ChartBuilder.Controls.Editing;

namespace ChartBuilder.Tests.Editing;

/// <summary>Where a vocal part's lyric bubbles go: above their notes, up a level where they'd cover each other (charter-spec C3 item 7).</summary>
[TestClass]
public sealed class LyricBubbleLayoutTests
{
    private const double Height = 16, Padding = 5, Line = 6, GapX = 4, GapY = 3;

    private static Rect Note(double left, double top = 100) => new(left, top, 20, 8);

    private static List<LyricBubble> Place(int levels, double minTop, params LyricBubbleInput[] inputs) =>
        LyricBubbleLayout.Place(inputs, Height, Padding, Line, GapX, GapY, levels, minTop);

    private static List<LyricBubble> Place(int levels, double minTop, double maxBottom, params LyricBubbleInput[] inputs) =>
        LyricBubbleLayout.Place(inputs, Height, Padding, Line, GapX, GapY, levels, minTop, maxBottom);

    [TestMethod]
    public void ALoneBubbleHoversAboveItsNoteWithALineDownToIt()
    {
        var bubble = Place(3, 0, new LyricBubbleInput(Note(50), 30)).Single();

        Assert.AreEqual(0, bubble.Level);
        Assert.AreEqual(new Rect(50, 100 - Line - Height, 40, Height), bubble.Bubble);
        Assert.AreEqual(bubble.Bubble.Bottom, bubble.LineFrom.Y, 1e-9);
        Assert.AreEqual(100, bubble.LineTo.Y, 1e-9);
        Assert.AreEqual(bubble.LineFrom.X, bubble.LineTo.X, 1e-9);
        Assert.IsTrue(bubble.LineFrom.X >= bubble.Bubble.Left && bubble.LineFrom.X <= bubble.Bubble.Right);
    }

    [TestMethod]
    public void BubblesFarEnoughApartShareALevel()
    {
        var bubbles = Place(3, 0, new LyricBubbleInput(Note(50), 30), new LyricBubbleInput(Note(200), 30));

        Assert.HasCount(2, bubbles);
        Assert.IsTrue(bubbles.All(b => b.Level == 0));
    }

    [TestMethod]
    public void ABubbleThatWouldCoverTheOneBeforeItIsMovedUpALevelAndItsLineStillReachesItsNote()
    {
        var bubbles = Place(3, 0, new LyricBubbleInput(Note(50), 60), new LyricBubbleInput(Note(70), 30));

        Assert.HasCount(2, bubbles);
        Assert.AreEqual(0, bubbles[0].Level);
        Assert.AreEqual(1, bubbles[1].Level);
        Assert.IsFalse(bubbles[0].Bubble.Intersects(bubbles[1].Bubble));
        Assert.AreEqual(Note(70).Top, bubbles[1].LineTo.Y, 1e-9);
        Assert.IsTrue(bubbles[1].LineFrom.Y < bubbles[1].LineTo.Y);
    }

    [TestMethod]
    public void BubblesAreLeftOutOnceNoLevelIsLeftForThem()
    {
        // Four notes a few pixels apart, with room for three levels: the fourth has none.
        var bubbles = Place(3, 0,
            new LyricBubbleInput(Note(50), 80), new LyricBubbleInput(Note(52), 80),
            new LyricBubbleInput(Note(54), 80), new LyricBubbleInput(Note(56), 80));

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, bubbles.Select(b => b.Index).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, bubbles.Select(b => b.Level).ToArray());
    }

    [TestMethod]
    public void ABubbleIsNotPlacedAboveTheLanesTopNorBelowItsBottomAndIsLeftOutWithNoRoom()
    {
        // A note near the top has room for the lowest level only, and none below.
        double minTop = 100 - Line - Height - 1;
        var bubbles = Place(3, minTop, 108, new LyricBubbleInput(Note(50), 80), new LyricBubbleInput(Note(52), 80));

        Assert.HasCount(1, bubbles);
        Assert.AreEqual(0, bubbles[0].Index);
        Assert.IsTrue(bubbles[0].Bubble.Top >= minTop);
    }

    [TestMethod]
    public void ANoteAtTheTopOfTheLaneHasItsBubbleBelowItWithTheLineUpToIt()
    {
        // Its top is the lane's: no room above.
        var note = Note(50, top: 100);
        var bubble = Place(3, 100, 300, new LyricBubbleInput(note, 30)).Single();

        Assert.AreEqual(0, bubble.Level);
        Assert.AreEqual(note.Bottom + Line, bubble.Bubble.Top, 1e-9);
        Assert.AreEqual(note.Bottom, bubble.LineTo.Y, 1e-9);
        Assert.AreEqual(bubble.Bubble.Top, bubble.LineFrom.Y, 1e-9);
    }

    [TestMethod]
    public void BubblesBelowTheTopNotesStackDownALevelWhereTheyWouldCoverEachOther()
    {
        var bubbles = Place(3, 100, 300, new LyricBubbleInput(Note(50, top: 100), 60), new LyricBubbleInput(Note(70, top: 100), 30));

        Assert.HasCount(2, bubbles);
        Assert.AreEqual(1, bubbles[1].Level);
        Assert.IsGreaterThan(bubbles[0].Bubble.Bottom, bubbles[1].Bubble.Top);
        Assert.AreEqual(108, bubbles[1].LineTo.Y, 1e-9);
    }

    [TestMethod]
    public void ANoteOnAHigherRowGivesABubbleThatDoesNotCoverALowerNotesBubbleBeyondIt()
    {
        // The second note is higher, so its bubble is high above the first's, whatever the overlap in time.
        var bubbles = Place(3, 0, new LyricBubbleInput(Note(50, top: 100), 60), new LyricBubbleInput(Note(70, top: 60), 30));

        Assert.HasCount(2, bubbles);
        Assert.AreEqual(0, bubbles[1].Level);
        Assert.IsFalse(bubbles[0].Bubble.Intersects(bubbles[1].Bubble));
    }

    [TestMethod]
    public void NoInputsPlaceNoBubbles()
    {
        Assert.IsEmpty(Place(3, 0));
    }
}
