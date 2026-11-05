using Avalonia;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// How long a note standing up on a perspective highway is still seen past the host's bottom edge: its box rises from its floor, so
/// it outlasts the floor's middle, which is what a note's time says. The editor draws notes up to that reach, so they leave under the
/// edge and not before it.
/// </summary>
[TestClass]
public sealed class UprightGemReachTests
{
    private const double Depth = 24;

    private static (UprightGemPainter Painter, PerspectiveCamera Camera) Painter(double width = 420, double height = 760)
    {
        var camera = new PerspectiveCamera(width, height);
        return (new UprightGemPainter(new GemPainter()) { Camera = camera }, camera);
    }

    // A bar across the lanes: wide enough for the painter to take for one, and the tallest thing that stands.
    private static Rect Bar(PerspectiveCamera camera, double middleY) =>
        new(20, middleY - (Depth / 2), camera.HostWidth - 40, Depth);

    [TestMethod]
    public void ANoteIsSeenWhileItsFloorsMiddleIsPastTheBottomEdge()
    {
        var (painter, camera) = Painter();

        Assert.IsGreaterThan(0, painter.ReachBelow(Depth));
        Assert.IsNotNull(painter.SilhouetteOf(Bar(camera, camera.PlaneHeight + 1)), "Its middle is past the edge, and most of it is not.");
    }

    [TestMethod]
    public void TheReachIsWhereTheTallestNoteHasGone()
    {
        foreach (double height in new[] { 400.0, 760, 1400 })
        {
            var (painter, camera) = Painter(height: height);
            double depth = GemDepth.For(height, perspective: true);
            double reach = painter.ReachBelow(depth);
            var bar = (double middle) => new Rect(20, middle - (depth / 2), camera.HostWidth - 40, depth);

            Assert.IsNotNull(painter.SilhouetteOf(bar(camera.PlaneHeight + reach - 1)), $"Still seen just inside the reach, host {height}.");
            Assert.IsNull(painter.SilhouetteOf(bar(camera.PlaneHeight + reach + 1)), $"Gone just past the reach, host {height}.");
        }
    }

    [TestMethod]
    public void ANoteNotQuiteAsTallIsGoneNoLaterThanTheReach()
    {
        var (painter, camera) = Painter();
        double reach = painter.ReachBelow(Depth);
        var gem = new Rect(100, camera.PlaneHeight + reach + 1 - (Depth / 2), 50, Depth);

        Assert.IsNull(painter.SilhouetteOf(gem));
    }

    [TestMethod]
    public void WithNoCameraThereIsNoReach()
    {
        var painter = new UprightGemPainter(new GemPainter());

        Assert.AreEqual(0, painter.ReachBelow(Depth));
    }
}
