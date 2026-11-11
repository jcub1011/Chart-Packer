using ChartShared.Core.Optimization;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>A savings estimate as the rows the Optimize window shows.</summary>
[TestClass]
public sealed class SavingsTableTests
{
    private static SavingsEstimate Estimate(params CodecSavings[] codecs) =>
        new(OptimizeStrategy.Video, SamplingMode.RandomSample, ChartsSampled: 2, ChartsTotal: 10, codecs);

    [TestMethod]
    public void EachCodecIsARowLargestLibrarySizeFirst()
    {
        var table = SavingsTable.From(Estimate(
            new CodecSavings("video/vp8", 1, 2, 1000, 500, 2000),
            new CodecSavings("video/h264", 2, 8, 4000, 1000, 16000)), includeCompatibility: true);

        CollectionAssert.AreEqual(new[] { "video/h264", "video/vp8" }, table.Rows.Select(r => r.CodecKey).ToArray());
        Assert.AreEqual(12000, table.Rows[0].EstimatedSavingBytes);
        Assert.AreEqual(1000, table.Rows[1].EstimatedSavingBytes);
        Assert.AreEqual(13000, table.TotalSavingBytes);
    }

    [TestMethod]
    public void TheSavingPerChartIsTheSampledSavingOverTheChartsSampled()
    {
        var table = SavingsTable.From(Estimate(new CodecSavings("video/h264", 2, 8, 4000, 1000, 16000)), includeCompatibility: true);

        Assert.AreEqual(1500, table.Rows[0].SavingPerChartBytes);
    }

    [TestMethod]
    public void ACodecNothingWasEstimatedForHasNoSavingPerChart()
    {
        var table = SavingsTable.From(Estimate(new CodecSavings("video/mpeg", 0, 3, 0, 0, 9000)), includeCompatibility: true);

        Assert.IsNull(table.Rows[0].SavingPerChartBytes);
        Assert.AreEqual(0, table.Rows[0].EstimatedSavingBytes);
    }

    [TestMethod]
    public void ACompatibilityConversionThatGrowsIsACost()
    {
        var table = SavingsTable.From(Estimate(
            new CodecSavings("video/h264", 2, 2, 1000, 500, 1000),
            new CodecSavings("video/vp9", 2, 2, 1000, 1500, 1000)), includeCompatibility: true);

        Assert.IsTrue(table.Rows.Single(r => r.CodecKey == "video/vp9").IsCompatibilityConversion);
        Assert.IsFalse(table.Rows.Single(r => r.CodecKey == "video/h264").IsCompatibilityConversion);
        Assert.AreEqual(500, table.CompatibilityCostBytes);
        Assert.AreEqual(0, table.TotalSavingBytes, "500 saved, 500 spent");
    }

    [TestMethod]
    public void WithoutCompatibilityTheTotalLeavesConversionsOut()
    {
        var table = SavingsTable.From(Estimate(
            new CodecSavings("video/h264", 2, 2, 1000, 500, 1000),
            new CodecSavings("video/vp9", 2, 2, 1000, 1500, 1000)), includeCompatibility: false);

        Assert.AreEqual(500, table.TotalSavingBytes);
        Assert.AreEqual(500, table.CompatibilityCostBytes, "the cost is still shown");
        Assert.AreEqual(2, table.Rows.Count);
    }

    [TestMethod]
    public void NoCodecsGivesAnEmptyTable()
    {
        var table = SavingsTable.From(Estimate(), includeCompatibility: true);

        Assert.AreEqual(0, table.Rows.Count);
        Assert.AreEqual(0, table.TotalSavingBytes);
    }
}
