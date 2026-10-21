using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>The fretting hand's cost of moving between six-fret lane states, three columns of two (docs/difficulty-model.md §14.2).</summary>
[TestClass]
public sealed class GridTransitionCostTests
{
    private const byte Open = 0;
    private const byte W1 = 1, W2 = 2, W3 = 4, B1 = 8, B2 = 16, B3 = 32;
    private static readonly DifficultyParameters P = DifficultyParameters.For(DifficultyInstrument.SixFret);

    [TestMethod]
    public void ABarreIsOneFingerInOneColumn()
    {
        Assert.AreEqual(1, GridTransitionCost.Span(W1 | B1));
        Assert.AreEqual(P.ChordNoteCost + P.BarreCost, GridTransitionCost.Shape(W1 | B1, P), 1e-9);
        Assert.AreEqual(P.FingerCost + P.ChordNoteCost + P.BarreCost, GridTransitionCost.Between(W1, W1 | B1, P), 1e-9);
    }

    [TestMethod]
    public void SwitchingRowsInAColumnCostsLessThanChangingColumn()
    {
        Assert.AreEqual(P.GridRowCost, GridTransitionCost.Between(W2, B2, P), 1e-9);
        Assert.AreEqual((2 * P.FingerCost) + P.TravelCost, GridTransitionCost.Between(W1, W2, P), 1e-9);
        Assert.IsLessThan(GridTransitionCost.Between(W1, W2, P), GridTransitionCost.Between(W1, B1, P));
    }

    [TestMethod]
    public void TravelIsMeasuredInColumns()
    {
        Assert.AreEqual(2, GridTransitionCost.Centre(B3, P));
        Assert.AreEqual(P.TravelCost, GridTransitionCost.Between(W1, W3, P) - GridTransitionCost.Between(W1, W2, P), 1e-9);
        Assert.AreEqual(GridTransitionCost.Between(W1, W3, P), GridTransitionCost.Between(B1, B3, P), 1e-9);
    }

    [TestMethod]
    public void AChordOfBothRowsInDifferentColumnsCostsMore()
    {
        Assert.AreEqual(3, GridTransitionCost.Span(W1 | B3));
        Assert.AreEqual((2 * P.ChordNoteCost) + P.MixedRowCost + P.ChordGapCost, GridTransitionCost.Shape(W1 | B3, P), 1e-9);
        Assert.IsGreaterThan(GridTransitionCost.Shape(W1 | W3, P), GridTransitionCost.Shape(W1 | B3, P));
    }

    [TestMethod]
    public void GoingToAnOpenNoteCostsTheOpenCostFromTheMiddleColumn()
    {
        Assert.AreEqual(P.FingerCost + P.OpenCost, GridTransitionCost.Between(Open, W2, P), 1e-9);
        Assert.AreEqual(P.FingerCost + P.OpenCost + P.TravelCost, GridTransitionCost.Between(B1, Open, P), 1e-9);
    }
}
