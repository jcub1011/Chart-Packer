using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>Scores to ranks: 0 to 6 for realistic charts, 7 and up for overcharts (docs/difficulty-model.md §7).</summary>
[TestClass]
public sealed class RankCurveTests
{
    [TestMethod]
    [DataRow(-5.0, 0.0)]
    [DataRow(0.0, 0.0)]
    [DataRow(7.5, 0.5)]
    [DataRow(15.0, 1.0)]
    [DataRow(21.5, 1.5)]
    [DataRow(80.0, 6.0)]
    [DataRow(90.0, 6.5)]
    [DataRow(100.0, 7.0)]
    [DataRow(130.0, 8.5)]
    public void TheCurveIsPiecewiseLinearAndGoesOnPastTheSoftCap(double score, double rank) => Assert.AreEqual(rank, RankCurve.Continuous(score), 1e-9);

    [TestMethod]
    public void TheTierIsTheWholeRank()
    {
        Assert.AreEqual(0, RankCurve.Tier(14.9));
        Assert.AreEqual(6, RankCurve.Tier(99.9));
        Assert.AreEqual(7, RankCurve.Tier(100));
        Assert.AreEqual(9, RankCurve.Tier(141));
    }

    [TestMethod]
    public void RanksPastTheSoftCapAreAsWideAsRankSix()
    {
        Assert.AreEqual(80, RankCurve.ThresholdOf(6));
        Assert.AreEqual(100, RankCurve.ThresholdOf(7));
        Assert.AreEqual(120, RankCurve.ThresholdOf(8));
        Assert.AreEqual(0, RankCurve.ThresholdOf(0));
    }
}
