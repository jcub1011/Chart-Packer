using ChartPacker.Core.Theming;

namespace ChartPacker.Core.Tests.Theming;

[TestClass]
public sealed class OklabTests
{
    [TestMethod]
    public void RedMatchesThePublishedValues()
    {
        var red = Oklab.FromSrgb(255, 0, 0);

        Assert.AreEqual(0.62796, red.L, 1e-3);
        Assert.AreEqual(0.22486, red.A, 1e-3);
        Assert.AreEqual(0.12585, red.B, 1e-3);
    }

    [TestMethod]
    public void BlackAndWhiteAreGreyAtTheEnds()
    {
        var black = Oklab.FromSrgb(0, 0, 0);
        var white = Oklab.FromSrgb(255, 255, 255);

        Assert.AreEqual(0, black.L, 1e-6);
        Assert.AreEqual(1, white.L, 1e-3);
        Assert.AreEqual(0, white.Chroma, 1e-3);
    }

    [TestMethod]
    public void SrgbRoundTripsExactly()
    {
        for (int r = 0; r <= 255; r += 17)
        {
            for (int g = 0; g <= 255; g += 17)
            {
                for (int b = 0; b <= 255; b += 17)
                {
                    var colour = new ThemeColor(255, (byte)r, (byte)g, (byte)b);

                    Assert.AreEqual(colour, Oklab.FromSrgb(colour).ToSrgb());
                }
            }
        }
    }

    [TestMethod]
    public void ToSrgbKeepsTheAlphaGiven()
    {
        Assert.AreEqual(0x40, Oklab.FromSrgb(10, 20, 30).ToSrgb(0x40).A);
    }

    [TestMethod]
    public void HueRunsFromZeroTo360()
    {
        var colour = Oklab.FromLch(0.6, 0.1, 300);

        Assert.AreEqual(300, colour.Hue, 1e-9);
        Assert.AreEqual(0.1, colour.Chroma, 1e-9);
    }

    [TestMethod]
    public void FromLchInGamutKeepsLightnessAndHueAndLowersChroma()
    {
        Assert.IsFalse(Oklab.FromLch(0.9, 0.3, 30).IsInGamut);

        var colour = Oklab.FromLchInGamut(0.9, 0.3, 30);

        Assert.IsTrue(colour.IsInGamut);
        Assert.AreEqual(0.9, colour.L, 1e-9);
        Assert.AreEqual(30, colour.Hue, 1e-6);
        Assert.IsLessThan(0.3, colour.Chroma);
    }

    [TestMethod]
    public void FromLchInGamutLeavesAColourInGamutAlone()
    {
        Assert.AreEqual(Oklab.FromLch(0.6, 0.05, 120), Oklab.FromLchInGamut(0.6, 0.05, 120));
    }

    [TestMethod]
    public void ContrastRunsFromOneTo21()
    {
        var black = new ThemeColor(255, 0, 0, 0);

        Assert.AreEqual(21, ThemeColor.Contrast(ThemeColor.White, black), 1e-9);
        Assert.AreEqual(21, ThemeColor.Contrast(black, ThemeColor.White), 1e-9);
        Assert.AreEqual(1, ThemeColor.Contrast(black, black), 1e-9);
    }

    [TestMethod]
    public void ArgbRoundTrips()
    {
        var colour = ThemeColor.FromArgb(0x408B5CF6);

        Assert.AreEqual(new ThemeColor(0x40, 0x8B, 0x5C, 0xF6), colour);
        Assert.AreEqual(0x408B5CF6u, colour.ToArgb());
        Assert.AreEqual("#408B5CF6", colour.ToString());
    }
}
