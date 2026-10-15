using Avalonia;
using ChartShared.UI.Views;

namespace ChartBuilder.Tests.Windows;

/// <summary>A window's normal bounds as read back from settings, and where their middle is on a screen.</summary>
[TestClass]
public sealed class WindowBoundsTests
{
    [TestMethod]
    public void ReadsBoundsSavedWhole()
    {
        var bounds = WindowBounds.FromSaved(-1200, 40, 1280, 800);

        Assert.AreEqual(new WindowBounds(new PixelPoint(-1200, 40), new Size(1280, 800)), bounds);
    }

    [TestMethod]
    public void DropsBoundsMissingAnyPart()
    {
        Assert.IsNull(WindowBounds.FromSaved(null, 40, 1280, 800));
        Assert.IsNull(WindowBounds.FromSaved(100, null, 1280, 800));
        Assert.IsNull(WindowBounds.FromSaved(100, 40, null, 800));
        Assert.IsNull(WindowBounds.FromSaved(100, 40, 1280, null));
    }

    [TestMethod]
    public void DropsBoundsWhoseSizeIsntAPositiveNumber()
    {
        Assert.IsNull(WindowBounds.FromSaved(100, 40, double.NaN, 800));
        Assert.IsNull(WindowBounds.FromSaved(100, 40, 1280, double.PositiveInfinity));
        Assert.IsNull(WindowBounds.FromSaved(100, 40, 0, 800));
        Assert.IsNull(WindowBounds.FromSaved(100, 40, 1280, -5));
    }

    [TestMethod]
    public void ClampsASizeBiggerThanAnyScreen()
    {
        var bounds = WindowBounds.FromSaved(100, 40, 20000, 9000);

        Assert.AreEqual(new WindowBounds(new PixelPoint(100, 40), WindowBounds.MaxSize), bounds);
    }

    [TestMethod]
    public void ReadsASizeSavedWhole()
    {
        Assert.AreEqual(new Size(820, 560), WindowBounds.SizeFromSaved(820, 560));
    }

    [TestMethod]
    public void DropsASizeMissingEitherPart()
    {
        Assert.IsNull(WindowBounds.SizeFromSaved(null, 560));
        Assert.IsNull(WindowBounds.SizeFromSaved(820, null));
    }

    [TestMethod]
    public void DropsASizeThatIsntAPositiveNumber()
    {
        Assert.IsNull(WindowBounds.SizeFromSaved(double.NaN, 560));
        Assert.IsNull(WindowBounds.SizeFromSaved(820, double.NegativeInfinity));
        Assert.IsNull(WindowBounds.SizeFromSaved(0, 560));
        Assert.IsNull(WindowBounds.SizeFromSaved(820, -5));
    }

    [TestMethod]
    public void ClampsASavedSizeBiggerThanAnyScreen()
    {
        Assert.AreEqual(WindowBounds.MaxSize, WindowBounds.SizeFromSaved(20000, 9000));
        Assert.AreEqual(new Size(WindowBounds.MaxSize.Width, 560), WindowBounds.SizeFromSaved(20000, 560));
    }

    [TestMethod]
    public void FindsTheMiddleWithTheSizeScaledAsTheScreenShowsIt()
    {
        var bounds = new WindowBounds(new PixelPoint(-100, 50), new Size(800, 600));

        Assert.AreEqual(new PixelPoint(300, 350), bounds.MiddleOn(1));
        Assert.AreEqual(new PixelPoint(700, 650), bounds.MiddleOn(2));
    }
}
