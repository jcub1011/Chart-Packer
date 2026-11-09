using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Editing;

/// <summary>How deep a highway's gems are along its time axis: in proportion to the lane's length, never its width.</summary>
[TestClass]
public sealed class GemDepthTests
{
    [TestMethod]
    public void ATallerLaneHasDeeperGemsFlatAndInPerspective()
    {
        // Lengths short of each one's most: about 580 pixels flat, a host 450 tall in perspective.
        foreach (var (perspective, lengths) in new[] { (false, new[] { 400.0, 480, 560 }), (true, [250.0, 350, 430]) })
        {
            double shorter = GemDepth.For(lengths[0], perspective);
            double middle = GemDepth.For(lengths[1], perspective);
            double taller = GemDepth.For(lengths[2], perspective);

            Assert.IsLessThan(middle, shorter, $"Perspective {perspective}: {lengths[0]} against {lengths[1]}.");
            Assert.IsLessThan(taller, middle, $"Perspective {perspective}: {lengths[1]} against {lengths[2]}.");
        }
    }

    [TestMethod]
    public void TheDepthIsInProportionToTheLengthBetweenItsLimits()
    {
        double flat = GemDepth.For(360, perspective: false);
        Assert.AreEqual(flat * 1.5, GemDepth.For(540, perspective: false), 1e-9);

        double tilted = GemDepth.For(250, perspective: true);
        Assert.AreEqual(tilted * 1.5, GemDepth.For(375, perspective: true), 1e-9);
    }

    [TestMethod]
    public void ALanesDepthIsKeptBetweenALeastAndAMost()
    {
        foreach (bool perspective in new[] { false, true })
        {
            Assert.AreEqual(8, GemDepth.For(0, perspective), 1e-9, $"Perspective {perspective}: no length.");
            Assert.AreEqual(8, GemDepth.For(50, perspective), 1e-9, $"Perspective {perspective}: a stub of a lane.");
        }

        Assert.AreEqual(14, GemDepth.For(100_000, perspective: false), 1e-9, "A huge flat lane.");
        Assert.AreEqual(18, GemDepth.For(100_000, perspective: true), 1e-9, "A huge perspective highway.");
    }

    [TestMethod]
    public void ATallLanesGemsStopGrowingSoTheyNeverBecomeBlocks()
    {
        foreach (bool perspective in new[] { false, true })
        {
            double tall = GemDepth.For(1100, perspective);
            double taller = GemDepth.For(2500, perspective);

            Assert.AreEqual(tall, taller, 1e-9, $"Perspective {perspective}: past the limit a taller lane has no deeper gems.");
        }

        // A lane short of the limit is still in proportion.
        Assert.IsLessThan(18, GemDepth.For(400, perspective: true));
        Assert.IsLessThan(14, GemDepth.For(560, perspective: false));
    }

    [TestMethod]
    public void ATypicalLanesGemsAreAtTheirMost()
    {
        // The lane the editor is usually in: about 760 pixels tall. Its gems are 14 deep flat, and 18 in perspective.
        Assert.AreEqual(14, GemDepth.For(760, perspective: false), 1e-9);
        Assert.AreEqual(18, GemDepth.For(760, perspective: true), 1e-9);
    }
}
