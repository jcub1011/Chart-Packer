using ChartShared.Audio.Analysis;
using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Charting;

/// <summary>How the spectrogram's pictures are sized to the screen (charter-spec C13).</summary>
[TestClass]
public sealed class SpectrogramPainterTests
{
    [TestMethod]
    [DataRow(1.0, 0)]
    [DataRow(1.5, 1)]
    [DataRow(2.0, 1)]
    [DataRow(3.0, 2)]
    [DataRow(1000.0, 10)]
    [DataRow(0.5, -1)]
    [DataRow(0.3, -1)]
    [DataRow(0.25, -2)]
    [DataRow(0.01, -2)]
    public void APicturesPixelIsAsManyColumnsAsFitInAPixelOfTheScreen(double columnsPerPixel, int level) =>
        Assert.AreEqual(level, SpectrogramPainter.LevelFor(columnsPerPixel));

    [TestMethod]
    public void TheLevelDoesntFlipAHairUnderAPowerOfTwo()
    {
        Assert.AreEqual(1, SpectrogramPainter.LevelFor(2 + 1e-9));
        Assert.AreEqual(SpectrogramPainter.MaxLevel, SpectrogramPainter.LevelFor(1e12));
        Assert.AreEqual(SpectrogramPainter.MinLevel, SpectrogramPainter.LevelFor(0));
    }

    [TestMethod]
    public void APictureIsOfAPowerOfTwoOfColumns()
    {
        Assert.AreEqual(256, SpectrogramPainter.ColumnsIn(0));
        Assert.AreEqual(1024, SpectrogramPainter.ColumnsIn(2));
        Assert.AreEqual(64, SpectrogramPainter.ColumnsIn(-2));
    }

    [TestMethod]
    public void APictureIsOfTheRowsThatShowAtTheSizeTheyreShownAt()
    {
        // A piano roll zoomed to an octave: 85 rows or so over 600 pixels.
        double rows = 12 * SpectrogramRows.RowsPerSemitone;
        Assert.IsTrue(SpectrogramPainter.RowsShown(300, 300 + rows, 600 / rows, out int top, out int bottom, out int height));

        Assert.AreEqual(300 * 16, top);
        Assert.AreEqual((int)Math.Ceiling((300 + rows) * 16), bottom);
        Assert.AreEqual(608, height, "600 pixels, to the next multiple of 32.");
    }

    [TestMethod]
    public void RowsOffThePictureArentShownAndAPictureIsNoMoreThanItsMostPixels()
    {
        Assert.IsTrue(SpectrogramPainter.RowsShown(SpectrogramRows.Count + 50, -20, 10, out int top, out int bottom, out int height), "Either way round.");
        Assert.AreEqual(0, top);
        Assert.AreEqual(SpectrogramRows.Count * 16, bottom);
        Assert.AreEqual(SpectrogramPainter.MaxHeight, height);

        Assert.IsFalse(SpectrogramPainter.RowsShown(-30, -10, 4, out _, out _, out _), "Wholly above the picture.");
        Assert.IsFalse(SpectrogramPainter.RowsShown(10, 20, 0, out _, out _, out _));
    }

    [TestMethod]
    public void ASmallPictureIsStillAStepOfPixels()
    {
        Assert.IsTrue(SpectrogramPainter.RowsShown(0, SpectrogramRows.Count, 3.0 / SpectrogramRows.Count, out _, out _, out int height));
        Assert.AreEqual(SpectrogramPainter.HeightStep, height);
    }

    [TestMethod]
    public void ZoomedOutAPixelIsTheLoudestOfTheRowsItCovers()
    {
        // The whole picture in a quarter as many pixels as rows: four rows each, the highest rows first.
        var samples = new SpectrogramPainter.RowSample[SpectrogramRows.Count / 4];
        SpectrogramPainter.SampleRows(0, SpectrogramRows.Count, samples);

        Assert.AreEqual(new SpectrogramPainter.RowSample(SpectrogramRows.Count - 4, SpectrogramRows.Count - 1, -1), samples[0]);
        Assert.AreEqual(new SpectrogramPainter.RowSample(0, 3, -1), samples[^1]);
        for (int y = 1; y < samples.Length; y++)
            Assert.AreEqual(samples[y - 1].Low - 1, samples[y].High, "No row is left out, and none is in two.");
    }

    [TestMethod]
    public void ZoomedInAPixelIsBetweenTheTwoRowsItsBetween()
    {
        // Ten rows from the picture's top in 40 pixels: four pixels a row.
        var samples = new SpectrogramPainter.RowSample[40];
        SpectrogramPainter.SampleRows(0, 10, samples);
        int highest = SpectrogramRows.Count - 1;

        // The top row's middle is two pixels down: above it there's no row to be between.
        Assert.AreEqual(highest, samples[0].Nearest);
        Assert.AreEqual(highest - 1, samples[2].Low);
        Assert.AreEqual(highest, samples[2].High);
        Assert.AreEqual(0.875f, samples[2].Blend, 1e-6f);
        Assert.AreEqual(0.125f, samples[5].Blend, 1e-6f, "Past halfway to the next row down's middle.");
        Assert.AreEqual(highest - 1, samples[5].Nearest);
        Assert.IsTrue(samples.All(s => s.Blend is >= 0 and <= 1 && s.High - s.Low is 0 or 1));
    }
}
