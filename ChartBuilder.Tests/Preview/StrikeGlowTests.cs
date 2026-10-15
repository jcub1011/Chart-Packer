using ChartPacker.UI.Controls.Highway;

namespace ChartBuilder.Tests.Preview;

/// <summary>
/// How a column lights as a note reaches the strike line, in the editor's lanes and the highway alike: a plain note's
/// glow fades out at once; a sustain's fades steadily to <see cref="StrikeGlow.SustainEndLevel"/> as it ends, then quickly
/// out.
/// </summary>
[TestClass]
public sealed class StrikeGlowTests
{
    [TestMethod]
    public void APlainNoteFadesOutAtOnce()
    {
        Assert.AreEqual(1, StrikeGlow.Brightness(0, 0), 1e-9);
        Assert.AreEqual(0.5, StrikeGlow.Brightness(StrikeGlow.Seconds / 2, 0), 1e-9);
        Assert.AreEqual(0, StrikeGlow.Brightness(StrikeGlow.Seconds, 0), 1e-9);
        Assert.AreEqual(0, StrikeGlow.Brightness(1, 0), 1e-9);
    }

    [TestMethod]
    public void ASustainFadesToItsEndLevelAsItEnds()
    {
        const double length = 2;
        Assert.AreEqual(1, StrikeGlow.Brightness(0, length), 1e-9);
        Assert.AreEqual(1 - ((1 - StrikeGlow.SustainEndLevel) / 2), StrikeGlow.Brightness(length / 2, length), 1e-9);
        Assert.AreEqual(StrikeGlow.SustainEndLevel, StrikeGlow.Brightness(length - 1e-9, length), 1e-6);
        Assert.AreEqual(StrikeGlow.SustainEndLevel, StrikeGlow.Brightness(length, length), 1e-9);
    }

    [TestMethod]
    public void ASustainGoesOutQuicklyAfterItEnds()
    {
        const double length = 2;
        Assert.AreEqual(StrikeGlow.SustainEndLevel / 2, StrikeGlow.Brightness(length + (StrikeGlow.ReleaseSeconds / 2), length), 1e-9);
        Assert.AreEqual(0, StrikeGlow.Brightness(length + StrikeGlow.ReleaseSeconds, length), 1e-9);
        Assert.AreEqual(0, StrikeGlow.Brightness(length + 1, length), 1e-9);
    }

    [TestMethod]
    public void NothingIsLitBeforeTheNoteArrives() => Assert.AreEqual(0, StrikeGlow.Brightness(-0.01, 1), 1e-9);

    [TestMethod]
    public void TheGlowNeverBrightensAsItFades()
    {
        foreach (double length in (double[])[0, 0.05, 0.5, 3])
        {
            double last = double.MaxValue;
            for (double since = 0; since < length + 1; since += 0.001)
            {
                double glow = StrikeGlow.Brightness(since, length);
                Assert.IsLessThanOrEqualTo(last + 1e-12, glow);
                last = glow;
            }
        }
    }
}
