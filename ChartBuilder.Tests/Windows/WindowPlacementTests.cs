using Avalonia;
using ChartShared.UI.Views;

namespace ChartBuilder.Tests.Windows;

/// <summary>Where an editor goes when it's placed over where the window open was.</summary>
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

    [TestMethod]
    public void KeepsARestoredPositionInsideTheWorkingArea()
    {
        var size = new PixelSize(1280, 800);

        Assert.AreEqual(new PixelPoint(100, 100), WindowPlacement.PlaceAt(new PixelPoint(100, 100), size, Screen));
        Assert.AreEqual(new PixelPoint(0, 0), WindowPlacement.PlaceAt(new PixelPoint(-500, -500), size, Screen));
        Assert.AreEqual(new PixelPoint(640, 240), WindowPlacement.PlaceAt(new PixelPoint(1900, 1000), size, Screen));
    }

    [TestMethod]
    public void LeavesARestoredWindowSnappedToAnEdgeWhereItWas()
    {
        // Snapped to the left (past a taskbar there) or the right, the frame's invisible border hangs off the area.
        var area = new PixelRect(70, 0, 1850, 1040);
        var size = new PixelSize(1280, 800);
        const int frameLeft = 8;

        Assert.AreEqual(new PixelPoint(62, 0), WindowPlacement.PlaceAt(new PixelPoint(62, 0), size, area, frameLeft));
        Assert.AreEqual(new PixelPoint(632, 0), WindowPlacement.PlaceAt(new PixelPoint(632, 0), size, area, frameLeft));
    }

    [TestMethod]
    public void KeepsARestoredWindowsClientAreaInsideTheWorkingArea()
    {
        var size = new PixelSize(1280, 800);
        const int frameLeft = 8;

        Assert.AreEqual(new PixelPoint(-8, 0), WindowPlacement.PlaceAt(new PixelPoint(-500, -500), size, Screen, frameLeft));
        Assert.AreEqual(new PixelPoint(632, 240), WindowPlacement.PlaceAt(new PixelPoint(1900, 1000), size, Screen, frameLeft));
    }

    [TestMethod]
    public void PutsARestoredPositionTooBigForTheWorkingAreaAtItsTopLeft()
    {
        var position = WindowPlacement.PlaceAt(new PixelPoint(100, 100), new PixelSize(2560, 1440), Screen);

        Assert.AreEqual(new PixelPoint(0, 0), position);
    }

    [TestMethod]
    public void FitsARestoredSizeToTheWorkingArea()
    {
        var min = new PixelSize(720, 480);

        Assert.AreEqual(new PixelSize(1280, 800), WindowPlacement.FitSize(new PixelSize(1280, 800), min, Screen));
        Assert.AreEqual(new PixelSize(1920, 1040), WindowPlacement.FitSize(new PixelSize(2560, 1440), min, Screen));
        Assert.AreEqual(min, WindowPlacement.FitSize(new PixelSize(100, 100), min, Screen));
    }
}
