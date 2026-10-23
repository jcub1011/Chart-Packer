using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>How hard timing is to read (docs/difficulty-model.md §4.3).</summary>
[TestClass]
public sealed class RatioComplexityTests
{
    private static readonly DifficultyParameters P = DifficultyParameters.Default;

    [TestMethod]
    [DataRow(1.0, 0.0)]
    [DataRow(2.0, 1.0)]
    [DataRow(3.0, 1.585)]
    [DataRow(4.0, 2.0)]
    [DataRow(1.5, 2.585)]
    [DataRow(4.0 / 3, 3.585)]
    [DataRow(1.02, 0.0)]
    [DataRow(1.05, 0.404)]
    [DataRow(1.25, 2.919)]
    public void TheDocumentsTableHolds(double ratio, double cost) => Assert.AreEqual(cost, RatioComplexity.Cost(ratio, P), 0.002);

    [TestMethod]
    public void EveryCleanRatioPlayedExactlyScoresItsOwnTenneyHeight()
    {
        foreach (var (p, q) in new[] { (1, 1), (4, 3), (3, 2), (2, 1), (3, 1), (4, 1), (6, 1), (8, 1) })
            Assert.AreEqual(RatioComplexity.TenneyHeight(p, q), RatioComplexity.Cost((double)p / q, P), 1e-9, $"{p}:{q}");
    }

    [TestMethod]
    public void ARestOrNoIntervalBeforeCostsNothingAndTheCostIsCapped()
    {
        Assert.AreEqual(0, RatioComplexity.ChangeCost(250, double.PositiveInfinity, P));
        Assert.AreEqual(0, RatioComplexity.ChangeCost(1200, 300, P));
        Assert.AreEqual(1, RatioComplexity.ChangeCost(125, 250, P), 1e-9);

        // Near 5:1 the cost is about 4.9, past the cap.
        Assert.AreEqual(P.MaxRatioCost, RatioComplexity.ChangeCost(100, 500, P));
    }

    [TestMethod]
    [DataRow(0.0, SubdivisionFamily.Neutral)]
    [DataRow(2.5, SubdivisionFamily.Neutral)]
    [DataRow(1.25, SubdivisionFamily.Binary)]
    [DataRow(0.0625, SubdivisionFamily.Binary)]
    [DataRow(1.0 / 3, SubdivisionFamily.Ternary)]
    [DataRow(4 + (5.0 / 6), SubdivisionFamily.Ternary)]
    [DataRow(0.2, SubdivisionFamily.Other)]
    [DataRow(1.004, SubdivisionFamily.Neutral)]
    public void NotesAreSortedIntoFamilies(double beat, SubdivisionFamily family) => Assert.AreEqual(family, RatioComplexity.FamilyOf(beat, P));
}
