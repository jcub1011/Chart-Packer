using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>Scores to ranks: 0 to 6 for realistic charts, 7 and up for overcharts (docs/difficulty-model.md §7).</summary>
[TestClass]
public sealed class RankCurveTests
{
    [TestMethod]
    [DataRow(-5.0, 0.0)]
    [DataRow(0.0, 0.0)]
    [DataRow(1.25, 0.5)]
    [DataRow(2.5, 1.0)]
    [DataRow(6.5, 1.5)]
    [DataRow(53.0, 6.0)]
    [DataRow(76.5, 6.5)]
    [DataRow(100.0, 7.0)]
    [DataRow(170.5, 8.5)]
    public void TheCurveIsPiecewiseLinearAndGoesOnPastTheSoftCap(double score, double rank) => Assert.AreEqual(rank, RankCurve.Continuous(score), 1e-9);

    [TestMethod]
    public void TheTierIsTheWholeRank()
    {
        Assert.AreEqual(0, RankCurve.Tier(2.4));
        Assert.AreEqual(6, RankCurve.Tier(99.9));
        Assert.AreEqual(7, RankCurve.Tier(100));
        Assert.AreEqual(9, RankCurve.Tier(194));
    }

    [TestMethod]
    public void RanksPastTheSoftCapAreAsWideAsRankSix()
    {
        Assert.AreEqual(53, RankCurve.ThresholdOf(6));
        Assert.AreEqual(100, RankCurve.ThresholdOf(7));
        Assert.AreEqual(147, RankCurve.ThresholdOf(8));
        Assert.AreEqual(0, RankCurve.ThresholdOf(0));
    }
}
