using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Validation;

[TestClass]
public sealed class ChartLocationTests
{
    [TestMethod]
    public void AFindingCarriesItsChartLocation()
    {
        var location = new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 480, 960);

        var finding = new Finding("CHR001", Severity.Warning, "message", Location: location);

        Assert.AreEqual(location, finding.Location);
        Assert.AreEqual("CHR001", finding.RuleId);
        Assert.AreEqual(Severity.Warning, finding.Severity);
        Assert.AreEqual("message", finding.Message);
        Assert.IsNull(finding.Offset);
        Assert.IsNull(finding.Key);
    }

    [TestMethod]
    public void AFindingHasNoChartLocationByDefault()
    {
        var finding = new Finding(SngRules.Sng003, Severity.Error, "message", 12, "name");

        Assert.IsNull(finding.Location);
        Assert.AreEqual(12L, finding.Offset);
        Assert.AreEqual("name", finding.Key);
    }

    [TestMethod]
    public void RangesOnTheSamePartAndDifficultyThatShareTicksOverlap()
    {
        var a = L(ChartDifficulty.Hard, 100, 200);
        var b = L(ChartDifficulty.Hard, 150, 300);

        Assert.IsTrue(a.Overlaps(b));
        Assert.IsTrue(b.Overlaps(a));
    }

    [TestMethod]
    public void RangesThatTouchAtOneTickOverlap()
    {
        var a = L(ChartDifficulty.Hard, 100, 200);
        var b = L(ChartDifficulty.Hard, 200, 300);

        Assert.IsTrue(a.Overlaps(b));
        Assert.IsTrue(b.Overlaps(a));
    }

    [TestMethod]
    public void RangesWithAGapBetweenThemDoNotOverlap()
    {
        var a = L(ChartDifficulty.Hard, 100, 200);
        var b = L(ChartDifficulty.Hard, 201, 300);

        Assert.IsFalse(a.Overlaps(b));
        Assert.IsFalse(b.Overlaps(a));
    }

    [TestMethod]
    public void RangesOnDifferentPartsDoNotOverlap()
    {
        var a = new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 0, 100);
        var b = new ChartLocation(ChartInstrument.Bass, ChartDifficulty.Expert, 0, 100);

        Assert.IsFalse(a.Overlaps(b));
    }

    [TestMethod]
    public void ANullDifficultyOverlapsAnyDifficulty()
    {
        var shared = L(null, 0, 100);

        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            Assert.IsTrue(shared.Overlaps(L(difficulty, 50, 60)));
            Assert.IsTrue(L(difficulty, 50, 60).Overlaps(shared));
        }

        Assert.IsTrue(shared.Overlaps(L(null, 50, 60)));
    }

    [TestMethod]
    public void DifferentDifficultiesDoNotOverlap()
    {
        var a = L(ChartDifficulty.Easy, 0, 100);
        var b = L(ChartDifficulty.Expert, 0, 100);

        Assert.IsFalse(a.Overlaps(b));
    }

    [TestMethod]
    [DataRow(99L, false)]
    [DataRow(100L, true)]
    [DataRow(150L, true)]
    [DataRow(200L, true)]
    [DataRow(201L, false)]
    public void ARangeCoversItsTicksInclusively(long tick, bool expected) =>
        Assert.AreEqual(expected, L(ChartDifficulty.Medium, 100, 200).Covers(tick));

    [TestMethod]
    public void ASinglePointCoversOnlyItsTick()
    {
        var point = L(ChartDifficulty.Medium, 480, 480);

        Assert.IsTrue(point.Covers(480));
        Assert.IsFalse(point.Covers(479));
        Assert.IsFalse(point.Covers(481));
    }

    private static ChartLocation L(ChartDifficulty? difficulty, long tick, long endTick) =>
        new(ChartInstrument.Drums, difficulty, tick, endTick);
}
