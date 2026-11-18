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
    public void PlacingStartsFromTheFirstNoteBeforeThatNoBubbleBeforeItCanReach()
    {
        double[] lefts = [0, 10, 200, 210, 220, 230];
        Func<int, double> leftOf = i => lefts[i];

        // 190 between the second and third notes is past the reach of 100; the rest are closer.
        Assert.AreEqual(2, LyricBubbleLayout.IndependentFrom(5, leftOf, 100, 256));
        Assert.AreEqual(2, LyricBubbleLayout.IndependentFrom(2, leftOf, 100, 256));
        Assert.AreEqual(0, LyricBubbleLayout.IndependentFrom(1, leftOf, 100, 256));
        Assert.AreEqual(0, LyricBubbleLayout.IndependentFrom(0, leftOf, 100, 256));

        // No further back than it's let walk.
        Assert.AreEqual(4, LyricBubbleLayout.IndependentFrom(5, leftOf, 100, 1));
    }

    [TestMethod]
    public void BubblesPlacedFromWherePlacingCanStartGoWhereTheyWouldWithEveryBubbleBeforeThem()
    {
        // A run that stacks bubbles up, a gap past any bubble's reach, then another that does; the first notes are left
        // out as they scroll off, yet the second run's bubbles stay where they were.
        double[] lefts = [0, 12, 24, 36, 300, 310, 320, 330, 340];
        var inputs = lefts.Select(l => new LyricBubbleInput(Note(l), 80)).ToArray();
        double reach = 80 + (Padding * 2) + GapX;
        var all = Place(3, 0, inputs);

        for (int view = 0; view < lefts.Length; view++)
        {
            int start = LyricBubbleLayout.IndependentFrom(view, i => lefts[i], reach, 256);
            var some = Place(3, 0, inputs[start..]);
            var wanted = all.Where(b => b.Index >= view).Select(b => (b.Index, b.Bubble)).ToArray();
            var got = some.Where(b => b.Index + start >= view).Select(b => (b.Index + start, b.Bubble)).ToArray();
            CollectionAssert.AreEqual(wanted, got, $"from note {view}");
        }

        // Without it, leaving the first of the second run out moves the next one down a level.
        Assert.AreNotEqual(all.Single(b => b.Index == 5).Bubble, Place(3, 0, inputs[5..]).Single(b => b.Index == 0).Bubble);
    }

    [TestMethod]
    public void NoInputsPlaceNoBubbles()
    {
        Assert.IsEmpty(Place(3, 0));
    }
}
