using Avalonia;
using ChartPacker.UI.Controls.Highway;

namespace ChartBuilder.Tests.Preview;

/// <summary>
/// A ghost's edge and a short sustain's tail are drawn as the shapes of their dashes, worked out once, rather than with a
/// dashed pen: the dashes run from the outline's first point, a dash long then a gap, carrying on round its corners as a
/// dashed pen's do.
/// </summary>
[TestClass]
public sealed class DashedOutlineTests
{
    private static readonly Point[] Square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];

    [TestMethod]
    public void TheDashesRunFromTheFirstPointADashThenAGapAtATime()
    {
        var dashes = DashedOutline.Dashes(Square, 2, 3);

        // 40 pixels round: a dash every 5, the first from the corner it starts at.
        Assert.AreEqual(8, dashes.Count);
        AssertPoints([new(0, 0), new(2, 0)], dashes[0]);
        AssertPoints([new(5, 0), new(7, 0)], dashes[1]);
        AssertPoints([new(10, 0), new(10, 2)], dashes[2]);
        AssertPoints([new(0, 5), new(0, 3)], dashes[^1]);
    }

    [TestMethod]
    public void ADashCarriesOnRoundACorner()
    {
        var dashes = DashedOutline.Dashes(Square, 3, 1);

        // The third dash starts a pixel before the top right corner and ends two down the side.
        AssertPoints([new(8, 0), new(10, 0), new(10, 1)], dashes[2]);
    }

    [TestMethod]
    public void AsMuchIsDrawnAsADashedPenWould()
    {
        double drawn = 0;
        foreach (var dash in DashedOutline.Dashes(Square, 2, 1.5))
        {
            for (int i = 1; i < dash.Count; i++)
                drawn += Point.Distance(dash[i - 1], dash[i]);
        }

        // 11 whole dashes and gaps make 38.5 pixels; the 1.5 left over are a dash, cut short where the outline closes.
        Assert.AreEqual((11 * 2) + 1.5, drawn, 1e-9);
    }

    [TestMethod]
    public void AnOutlineWithAPointRepeatedDrawsTheSame()
    {
        Point[] repeated = [new(0, 0), new(10, 0), new(10, 0), new(10, 10), new(0, 10)];

        var dashes = DashedOutline.Dashes(repeated, 3, 1);

        Assert.AreEqual(DashedOutline.Dashes(Square, 3, 1).Count, dashes.Count);
        AssertPoints([new(8, 0), new(10, 0), new(10, 1)], dashes[2]);
    }

    [TestMethod]
    public void ADashOrGapOfNothingIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DashedOutline.Dashes(Square, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DashedOutline.Dashes(Square, 1, 0));
    }

    private static void AssertPoints(Point[] expected, IReadOnlyList<Point> actual)
    {
        Assert.AreEqual(expected.Length, actual.Count, $"Expected {string.Join(", ", expected)}; got {string.Join(", ", actual)}.");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].X, actual[i].X, 1e-9, $"Point {i}'s X.");
            Assert.AreEqual(expected[i].Y, actual[i].Y, 1e-9, $"Point {i}'s Y.");
        }
    }
}
