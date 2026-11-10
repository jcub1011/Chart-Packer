using ChartBuilder.Controls.Editing;

namespace ChartBuilder.Tests.Editing;

/// <summary>How wide a highway's columns are: a flat highway's stop at 64 pixels, a perspective one's together at 480.</summary>
[TestClass]
public sealed class PerspectiveHighwayWidthTests
{
    private const double Margins = 130;

    [TestMethod]
    public void AFlatHighwaysColumnsStopAtSixtyFourPixels()
    {
        Assert.AreEqual(64, HighwayEditorCanvas.LaneSizeFor(2000, Margins, 5, perspective: false), 1e-9);
        Assert.AreEqual(64, HighwayEditorCanvas.LaneSizeFor(2000, Margins, 3, perspective: false), 1e-9);
    }

    [TestMethod]
    public void APerspectiveHighwayIsAsWideAsTheRoomAllowsUpToFourHundredAndEightyPixels()
    {
        // Room for 5 x 96 = 480 and more: capped.
        Assert.AreEqual(96, HighwayEditorCanvas.LaneSizeFor(2000, Margins, 5, perspective: true), 1e-9);
        Assert.AreEqual(160, HighwayEditorCanvas.LaneSizeFor(2000, Margins, 3, perspective: true), 1e-9);

        // Short of room, it takes all there is, as a flat one does.
        Assert.AreEqual(34, HighwayEditorCanvas.LaneSizeFor(300, Margins, 5, perspective: true), 1e-9);
        Assert.AreEqual(70, HighwayEditorCanvas.LaneSizeFor(480, Margins, 5, perspective: true), 1e-9);
    }

    [TestMethod]
    public void AllTheColumnsOfAPerspectiveHighwayNeverPassFourHundredAndEightyPixels()
    {
        foreach (int count in new[] { 1, 3, 4, 5, 6 })
        {
            foreach (double width in new[] { 100.0, 300, 560, 760, 1500, 4000 })
                Assert.IsLessThanOrEqualTo(480 + 1e-9, HighwayEditorCanvas.LaneSizeFor(width, Margins, count, perspective: true) * count, $"{count} columns in {width}.");
        }
    }

    [TestMethod]
    public void APerspectiveHighwayIsNeverNarrowerThanAFlatOne()
    {
        foreach (int count in new[] { 3, 4, 5, 6 })
        {
            foreach (double width in new[] { 100.0, 300, 560, 760, 1500 })
                Assert.IsGreaterThanOrEqualTo(HighwayEditorCanvas.LaneSizeFor(width, Margins, count, perspective: false), HighwayEditorCanvas.LaneSizeFor(width, Margins, count, perspective: true), $"{count} columns in {width}.");
        }
    }

    [TestMethod]
    public void ARoomSmallerThanTheMarginsStillLeavesAColumnToDraw()
    {
        Assert.IsGreaterThan(0, HighwayEditorCanvas.LaneSizeFor(50, Margins, 5, perspective: true));
        Assert.IsGreaterThan(0, HighwayEditorCanvas.LaneSizeFor(0, Margins, 5, perspective: false));
    }
}
