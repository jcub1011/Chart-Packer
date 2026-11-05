using ChartShared.Core.Charting;

namespace ChartShared.Core.Tests.Charting;

[TestClass]
public sealed class HighwayPixelsPerSecondTests
{
    [TestMethod]
    public void ASpeedIsFromTwentyFiveToAThousandPixelsPerSecond()
    {
        Assert.AreEqual(25, HighwayPixelsPerSecond.Clamp(0), 1e-9);
        Assert.AreEqual(25, HighwayPixelsPerSecond.Clamp(-3), 1e-9);
        Assert.AreEqual(1000, HighwayPixelsPerSecond.Clamp(50000), 1e-9);
        Assert.AreEqual(150, HighwayPixelsPerSecond.Clamp(double.PositiveInfinity, fallback: 150), 1e-9, "Infinity isn't a number: the fallback.");
        Assert.AreEqual(HighwayPixelsPerSecond.Default, HighwayPixelsPerSecond.Clamp(double.NaN), 1e-9);
    }

    [TestMethod]
    public void ASpeedIsKeptToTheGrainOfItsPartOfTheRange()
    {
        Assert.AreEqual(35, HighwayPixelsPerSecond.Clamp(33), 1e-9, "Five below a hundred.");
        Assert.AreEqual(150, HighwayPixelsPerSecond.Clamp(152), 1e-9, "Ten from a hundred.");
        Assert.AreEqual(300, HighwayPixelsPerSecond.Clamp(300), 1e-9, "A value already on its grain stays.");
        Assert.AreEqual(525, HighwayPixelsPerSecond.Clamp(530), 1e-9, "Twenty-five from five hundred.");
        Assert.AreEqual(HighwayPixelsPerSecond.Default, HighwayPixelsPerSecond.Clamp(HighwayPixelsPerSecond.Default), 1e-9);
        Assert.AreEqual(150, HighwayPixelsPerSecond.Clamp(double.NaN), 1e-9, "A highway is 150 px/s unless set otherwise.");
    }

    [TestMethod]
    public void ANotchAlwaysMovesTheSpeedUntilItsEnd()
    {
        double value = HighwayPixelsPerSecond.Min;
        for (int i = 0; i < 500 && value < HighwayPixelsPerSecond.Max; i++)
        {
            double next = HighwayPixelsPerSecond.Stepped(value, 1);
            Assert.IsGreaterThan(value, next, $"A notch up from {value} moved nowhere.");
            value = next;
        }

        Assert.AreEqual(HighwayPixelsPerSecond.Max, value, 1e-9);

        for (int i = 0; i < 500 && value > HighwayPixelsPerSecond.Min; i++)
        {
            double next = HighwayPixelsPerSecond.Stepped(value, -1);
            Assert.IsLessThan(value, next, $"A notch down from {value} moved nowhere.");
            value = next;
        }

        Assert.AreEqual(HighwayPixelsPerSecond.Min, value, 1e-9);
        Assert.AreEqual(95, HighwayPixelsPerSecond.Stepped(100, -1), 1e-9, "Going down uses the finer grain of where it's going.");
        Assert.AreEqual(HighwayPixelsPerSecond.Max, HighwayPixelsPerSecond.Stepped(HighwayPixelsPerSecond.Max, 3), 1e-9, "Held at the end.");
    }

    [TestMethod]
    public void TheSliderScaleRoundTripsAndEndsAtTheRange()
    {
        foreach (double speed in new[] { 25.0, 100, 200, 300, 525, 1000 })
            Assert.AreEqual(speed, HighwayPixelsPerSecond.FromScale(HighwayPixelsPerSecond.ToScale(speed)), 1e-9);

        Assert.AreEqual(HighwayPixelsPerSecond.Min, HighwayPixelsPerSecond.FromScale(HighwayPixelsPerSecond.MinScale), 1e-9);
        Assert.AreEqual(HighwayPixelsPerSecond.Max, HighwayPixelsPerSecond.FromScale(HighwayPixelsPerSecond.MaxScale), 1e-9);
    }

    [TestMethod]
    public void ATallerHighwayShowsFurtherAheadAtTheSameSpeed()
    {
        Assert.AreEqual(2.0, HighwayPixelsPerSecond.LookAheadFor(600, 300), 1e-9);
        Assert.AreEqual(4.0, HighwayPixelsPerSecond.LookAheadFor(1200, 300), 1e-9, "Twice as tall, twice as far.");
        Assert.AreEqual(2.0, HighwayPixelsPerSecond.LookAheadFor(300, 150), 1e-9, "Half the speed, twice as far.");

        // So a second of the song is the same number of pixels, whatever the height.
        foreach (double height in new[] { 200.0, 500, 1400 })
            Assert.AreEqual(300, height / HighwayPixelsPerSecond.LookAheadFor(height, 300), 1e-6);
    }

    [TestMethod]
    public void AHighwayCanShowMoreThanTheLookAheadMaximumInPixelsPerSecond()
    {
        Assert.IsGreaterThan(HighwayLookAhead.Max, HighwayPixelsPerSecond.LookAheadFor(3000, 25));
    }

    [TestMethod]
    public void AVeryShortHighwayShowsNoLessThanTheShortestShownEverywhere()
    {
        double shown = HighwayPixelsPerSecond.LookAheadFor(5, 1000);
        Assert.AreEqual(HighwayLookAhead.ShortestShown, shown, 1e-9);

        // The span the editor marks is kept to the same minimum, so it matches what the lane draws.
        var viewport = new ChartShared.Core.Charting.Editing.EditorViewport();
        Assert.AreEqual(HighwayLookAhead.ShortestShown, viewport.SpanAhead(0.01).End - viewport.Cursor, 1e-9);
    }

    [TestMethod]
    public void AHighwayWithNoLengthYetShowsTheShortestLookAhead()
    {
        Assert.AreEqual(HighwayLookAhead.Min, HighwayPixelsPerSecond.LookAheadFor(0, 300), 1e-9);
        Assert.AreEqual(HighwayLookAhead.Min, HighwayPixelsPerSecond.LookAheadFor(double.NaN, 300), 1e-9);
    }

    [TestMethod]
    public void ALengthIsItsLookAheadAsItIsOrWhatItsPixelsMakeOfItsSpeed()
    {
        var seconds = new HighwayLength(HighwayLengthMode.LookAhead, 3, 300);
        var pixels = new HighwayLength(HighwayLengthMode.PixelsPerSecond, 3, 300);

        Assert.AreEqual(3, seconds.LookAheadFor(900), 1e-9, "The height doesn't matter in look-ahead mode.");
        Assert.AreEqual(3, seconds.LookAheadFor(100), 1e-9);
        Assert.IsFalse(seconds.IsPixelsPerSecond);
        Assert.AreEqual(3, pixels.LookAheadFor(900), 1e-9);
        Assert.AreEqual(1, pixels.LookAheadFor(300), 1e-9, "The height does in pixels per second mode.");
        Assert.IsTrue(pixels.IsPixelsPerSecond);
    }
}
