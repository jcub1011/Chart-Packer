using Avalonia;
using ChartPacker.UI.Views;

namespace ChartBuilder.Tests.Windows;

/// <summary>Where the launcher and editor go when one is placed over where the other was.</summary>
[TestClass]
public sealed class WindowPlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1040);

    [TestMethod]
    public void CentersAWindowThatFits()
    {
        var position = WindowPlacement.Centered(new PixelPoint(960, 520), new PixelSize(760, 520), Screen);

        Assert.AreEqual(new PixelPoint(580, 260), position);
    }

    [TestMethod]
    public void KeepsAWindowInsideTheWorkingArea()
    {
        var size = new PixelSize(1280, 800);

        Assert.AreEqual(new PixelPoint(0, 0), WindowPlacement.Centered(new PixelPoint(100, 100), size, Screen));
        Assert.AreEqual(new PixelPoint(640, 240), WindowPlacement.Centered(new PixelPoint(1900, 1000), size, Screen));
    }

    [TestMethod]
    public void PutsAWindowTooBigForTheWorkingAreaAtItsTopLeft()
    {
        var position = WindowPlacement.Centered(new PixelPoint(960, 520), new PixelSize(2560, 1440), Screen);

        Assert.AreEqual(new PixelPoint(0, 0), position);
    }

    [TestMethod]
    public void PlacesOnAScreenLeftOfAndAboveThePrimary()
    {
        var left = new PixelRect(-2560, -200, 2560, 1400);

        Assert.AreEqual(new PixelPoint(-1920, 200), WindowPlacement.Centered(new PixelPoint(-1280, 600), new PixelSize(1280, 800), left));
        Assert.AreEqual(new PixelPoint(-2560, -200), WindowPlacement.Centered(new PixelPoint(-2500, -150), new PixelSize(1280, 800), left));
        Assert.AreEqual(new PixelPoint(-1280, 400), WindowPlacement.Centered(new PixelPoint(10, 1300), new PixelSize(1280, 800), left));
    }
}
