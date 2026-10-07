using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class SnapGridTests
{
    public static IEnumerable<object[]> Resolutions => [[192], [480], [960]];

    [TestMethod]
    [DynamicData(nameof(Resolutions))]
    public void EveryCommonSnapLandsOnWholeTicksAtTheEditorsResolutions(int resolution)
    {
        foreach (int division in SnapGrid.CommonDivisions)
        {
            double step = SnapGrid.Of(division).StepTicks(resolution);
            Assert.AreEqual(Math.Round(step), step, $"1/{division} at {resolution}");
        }
    }

    [TestMethod]
    public void NearestSnapsToTheGridAndOffToTheTick()
    {
        var map = Map();

        Assert.AreEqual(120, SnapGrid.Of(16).Nearest(150, map));
        Assert.AreEqual(240, SnapGrid.Of(16).Nearest(181, map));
        Assert.AreEqual(160, SnapGrid.Of(12).Nearest(150, map));
        Assert.AreEqual(150, SnapGrid.Of(16, isOn: false).Nearest(150.4, map));
        Assert.AreEqual(0, SnapGrid.Of(16).Nearest(-50, map));
    }

    [TestMethod]
    public void TheGridStartsAgainOnEachTimeSignature()
    {
        // A 7/8 measure from 0, then 4/4 from 1680: quarter notes count from 1680, not from 0.
        var map = new TempoMap(480, [], [new TimeSignature(0, 7, 8), new TimeSignature(1680, 4, 4)]);
        var quarter = SnapGrid.Of(4);

        Assert.AreEqual(1680 + 480, quarter.Nearest(1680 + 500, map));
        Assert.AreEqual(1680, quarter.Step(1440, 1, map), "The grid ends at the next signature.");
        Assert.AreEqual(2160, quarter.Step(1680, 1, map));
        Assert.AreEqual(1440, quarter.Step(1680, -1, map));
    }

    [TestMethod]
    public void StepGoesToTheNextLineFirstThenWholeSteps()
    {
        var map = Map();
        var sixteenth = SnapGrid.Of(16);

        Assert.AreEqual(240, sixteenth.Step(130, 1, map));
        Assert.AreEqual(120, sixteenth.Step(130, -1, map));
        Assert.AreEqual(480, sixteenth.Step(120, 3, map));
        Assert.AreEqual(0, sixteenth.Step(120, -5, map));
    }

    [TestMethod]
    public void AFinerThanATickSnapStillMovesOn()
    {
        var map = new TempoMap(48, [], []);

        Assert.AreEqual(1, SnapGrid.Of(768).Step(0, 1, map));
        Assert.AreEqual(4, SnapGrid.Of(768).Step(0, 4, map));
    }

    [TestMethod]
    public void MeasuresStepByMeasureLines()
    {
        var map = Map();

        Assert.AreEqual(1920, SnapGrid.StepMeasures(100, 1, map));
        Assert.AreEqual(3840, SnapGrid.StepMeasures(1920, 1, map));
        Assert.AreEqual(1920, SnapGrid.StepMeasures(2000, -1, map));
        Assert.AreEqual(0, SnapGrid.StepMeasures(1920, -1, map));
    }

    [TestMethod]
    public void BracketsStepThroughTheCommonSnaps()
    {
        Assert.AreEqual(24, SnapGrid.Of(16).Finer().Division);
        Assert.AreEqual(12, SnapGrid.Of(16).Coarser().Division);
        Assert.AreEqual(1, SnapGrid.Of(1).Coarser().Division);
        Assert.AreEqual(192, SnapGrid.Of(192).Finer().Division);
        Assert.AreEqual(24, SnapGrid.Of(20).Finer().Division, "A custom snap steps to the next common one.");
    }

    private static TempoMap Map() => new(480, [], []);
}
