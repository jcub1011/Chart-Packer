using ChartShared.Core.Charting;

namespace ChartShared.Core.Tests.Charting;

[TestClass]
public sealed class HighwayLookAheadTests
{
    [TestMethod]
    public void ALookAheadIsFromHalfASecondToSixtySeconds()
    {
        Assert.AreEqual(0.5, HighwayLookAhead.Clamp(0), 1e-9);
        Assert.AreEqual(0.5, HighwayLookAhead.Clamp(-3), 1e-9);
        Assert.AreEqual(60, HighwayLookAhead.Clamp(1000), 1e-9);
        Assert.AreEqual(4, HighwayLookAhead.Clamp(double.PositiveInfinity, fallback: 4), 1e-9, "Infinity isn't a number: the fallback.");
    }

    [TestMethod]
    public void TheDefaultLookAheadIsOneThatCanBeSet()
    {
        Assert.AreEqual(HighwayLookAhead.Default, HighwayLookAhead.Clamp(HighwayLookAhead.Default), 1e-9, "Within range and on its grain.");
        Assert.AreEqual(HighwayLookAhead.Default, HighwayLookAhead.FromNoteSpeed(1, fallback: 9), 1e-9, "The normal note speed showed it.");
    }

    [TestMethod]
    public void ALookAheadThatIsNotANumberIsTheFallback()
    {
        Assert.AreEqual(1.4, HighwayLookAhead.Clamp(double.NaN, fallback: 1.4), 1e-9);
        Assert.AreEqual(0.5, HighwayLookAhead.Clamp(double.NaN), 1e-9, "The shortest, with no fallback given.");
        Assert.AreEqual(60, HighwayLookAhead.Clamp(double.NaN, fallback: 500), 1e-9, "The fallback is held to the range too.");
    }

    [TestMethod]
    public void ALookAheadIsKeptToTheGrainOfItsPartOfTheRange()
    {
        Assert.AreEqual(1.3, HighwayLookAhead.Clamp(1.26), 1e-9, "A tenth of a second below two seconds.");
        Assert.AreEqual(1.4, HighwayLookAhead.Clamp(1.4), 1e-9, "A value already on its grain stays.");
        Assert.AreEqual(2.5, HighwayLookAhead.Clamp(2.26), 1e-9, "Half a second from two to ten.");
        Assert.AreEqual(9.5, HighwayLookAhead.Clamp(9.7), 1e-9);
        Assert.AreEqual(12, HighwayLookAhead.Clamp(11.6), 1e-9, "A second from ten.");
        Assert.AreEqual(30, HighwayLookAhead.Clamp(30), 1e-9);
    }

    [TestMethod]
    public void TheSliderRunsOnALogarithmicScaleOverTheWholeRange()
    {
        Assert.AreEqual(HighwayLookAhead.MinScale, HighwayLookAhead.ToScale(HighwayLookAhead.Min), 1e-9);
        Assert.AreEqual(HighwayLookAhead.MaxScale, HighwayLookAhead.ToScale(HighwayLookAhead.Max), 1e-9);
        Assert.AreEqual(HighwayLookAhead.Min, HighwayLookAhead.FromScale(HighwayLookAhead.MinScale), 1e-9);
        Assert.AreEqual(HighwayLookAhead.Max, HighwayLookAhead.FromScale(HighwayLookAhead.MaxScale), 1e-9);

        // The same travel is the same ratio, so each doubling is as far along the slider as the last.
        double octave = HighwayLookAhead.ToScale(1) - HighwayLookAhead.ToScale(0.5);
        Assert.AreEqual(octave, HighwayLookAhead.ToScale(4) - HighwayLookAhead.ToScale(2), 1e-9);
        Assert.AreEqual(octave, HighwayLookAhead.ToScale(32) - HighwayLookAhead.ToScale(16), 1e-9);
    }

    [TestMethod]
    public void TheShortLookAheadsTakeUpAFairShareOfTheSlider()
    {
        // Under five seconds is nearly half of the slider's travel, where a linear one has under a tenth.
        double share = (HighwayLookAhead.ToScale(5) - HighwayLookAhead.MinScale) / (HighwayLookAhead.MaxScale - HighwayLookAhead.MinScale);
        Assert.IsGreaterThan(0.47, share);
        Assert.IsLessThan(0.49, share);
    }

    [TestMethod]
    public void AScaleValueIsSnappedToALookAheadItself()
    {
        Assert.AreEqual(1.4, HighwayLookAhead.FromScale(HighwayLookAhead.ToScale(1.4)), 1e-9);
        Assert.AreEqual(60, HighwayLookAhead.FromScale(100), 1e-9);
        Assert.AreEqual(0.5, HighwayLookAhead.FromScale(-100), 1e-9);
    }

    [TestMethod]
    public void ANotchAlwaysMovesALookAheadAndBackAgainWhereverItIs()
    {
        // From ten seconds up a half-second notch used to snap back to where it was, so scrolling in did nothing.
        foreach (double start in new[] { 0.5, 1.4, 2, 9.5, 10, 11, 25, 59 })
        {
            double up = HighwayLookAhead.Stepped(start, 1);
            Assert.IsGreaterThan(start, up, $"Up from {start}.");
            Assert.AreEqual(start, HighwayLookAhead.Stepped(up, -1), 1e-9, $"Back from {up}.");
        }

        Assert.AreEqual(9.5, HighwayLookAhead.Stepped(10, -1), 1e-9);
        Assert.AreEqual(11, HighwayLookAhead.Stepped(10, 1), 1e-9);
        Assert.AreEqual(10, HighwayLookAhead.Stepped(11, -1), 1e-9);
        Assert.AreEqual(1.9, HighwayLookAhead.Stepped(1.4, 1), 1e-9);
        Assert.AreEqual(12, HighwayLookAhead.Stepped(10, 2), 1e-9);
        Assert.AreEqual(60, HighwayLookAhead.Stepped(60, 1), 1e-9, "Stops at the longest.");
        Assert.AreEqual(0.5, HighwayLookAhead.Stepped(0.5, -1), 1e-9, "Stops at the shortest.");
    }

    [TestMethod]
    public void ANoteSpeedBecomesTheLookAheadItShowed()
    {
        Assert.AreEqual(1.4, HighwayLookAhead.FromNoteSpeed(1, fallback: 9), 1e-9, "The normal speed showed 1.4 seconds.");
        Assert.AreEqual(0.7, HighwayLookAhead.FromNoteSpeed(2, fallback: 9), 1e-9, "Twice as fast shows half as far.");
        Assert.AreEqual(3, HighwayLookAhead.FromNoteSpeed(0.5, fallback: 9), 1e-9, "2.8 seconds, to the half second.");
        Assert.AreEqual(9, HighwayLookAhead.FromNoteSpeed(0, fallback: 9), 1e-9, "Not a speed: the fallback.");
        Assert.AreEqual(9, HighwayLookAhead.FromNoteSpeed(double.NaN, fallback: 9), 1e-9);
    }
}
