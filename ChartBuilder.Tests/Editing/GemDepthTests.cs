using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Editing;

/// <summary>How deep a highway's gems are along its time axis: in proportion to the lane's length, never its width.</summary>
[TestClass]
public sealed class GemDepthTests
{
    [TestMethod]
    public void ATallerLaneHasDeeperGemsFlatAndInPerspective()
    {
        foreach (bool perspective in new[] { false, true })
        {
            double shorter = GemDepth.For(300, perspective);
            double middle = GemDepth.For(450, perspective);
            double taller = GemDepth.For(600, perspective);

            Assert.IsLessThan(middle, shorter, $"Perspective {perspective}: 300 against 450.");
            Assert.IsLessThan(taller, middle, $"Perspective {perspective}: 450 against 600.");
        }
    }

    [TestMethod]
    public void TheDepthIsInProportionToTheLengthBetweenItsLimits()
    {
        double flat = GemDepth.For(400, perspective: false);
        Assert.AreEqual(flat * 1.5, GemDepth.For(600, perspective: false), 1e-9);

        double tilted = GemDepth.For(400, perspective: true);
        Assert.AreEqual(tilted * 1.5, GemDepth.For(600, perspective: true), 1e-9);
    }

    [TestMethod]
    public void ALanesDepthIsKeptBetweenALeastAndAMost()
    {
        foreach (bool perspective in new[] { false, true })
        {
            Assert.AreEqual(8, GemDepth.For(0, perspective), 1e-9, $"Perspective {perspective}: no length.");
            Assert.AreEqual(8, GemDepth.For(50, perspective), 1e-9, $"Perspective {perspective}: a stub of a lane.");
            Assert.IsLessThanOrEqualTo(24, GemDepth.For(100_000, perspective), $"Perspective {perspective}: a huge lane.");
        }

        Assert.AreEqual(24, GemDepth.For(100_000, perspective: false), 1e-9);
        Assert.AreEqual(24, GemDepth.For(100_000, perspective: true), 1e-9);
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
        Assert.IsLessThan(24, GemDepth.For(500, perspective: true));
        Assert.IsLessThan(24, GemDepth.For(500, perspective: false));
    }

    [TestMethod]
    public void ATypicalLaneKeepsGemsAboutAsDeepAsTheyWere()
    {
        // The lane the editor is usually in: about 760 pixels tall. Gems were 18 deep flat, and are 24 at most in perspective.
        Assert.AreEqual(18, GemDepth.For(760, perspective: false), 0.5);
        Assert.AreEqual(24, GemDepth.For(760, perspective: true), 1e-9);
    }
}
