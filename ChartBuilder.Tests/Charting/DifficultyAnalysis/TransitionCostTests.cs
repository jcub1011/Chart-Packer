using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>The fretting hand's cost of moving between lane states (docs/difficulty-model.md §4.2).</summary>
[TestClass]
public sealed class TransitionCostTests
{
    private const byte Open = 0;
    private const byte G = 1, R = 2, Y = 4, B = 8, O = 16;
    private static readonly DifficultyParameters P = DifficultyParameters.Default;

    [TestMethod]
    [DataRow(G, R, 1.3)]
    [DataRow(G, Y, 1.6)]
    [DataRow(G, O, 2.2)]
    [DataRow(Open, Y, 1.75)]
    [DataRow(Open, G, 2.35)]
    [DataRow((byte)(G | R), (byte)(R | Y), 1.9)]
    [DataRow((byte)(G | R), (byte)(Y | B), 3.2)]
    public void TheDocumentsWorkedCostsHold(byte from, byte to, double cost)
    {
        Assert.AreEqual(cost, TransitionCost.Between(from, to, P), 1e-9);
        Assert.AreEqual(cost, TransitionCost.Between(to, from, P), 1e-9);
    }

    [TestMethod]
    public void OpenNoteAlternationCostsMoreThanLanesUpToTwoApart()
    {
        double cheapestOpen = new[] { G, R, Y, B, O }.Min(l => TransitionCost.Between(Open, l, P));

        Assert.IsGreaterThan(TransitionCost.Between(G, Y, P), cheapestOpen);
        Assert.IsGreaterThan(TransitionCost.Between(R, B, P), cheapestOpen);
    }

    [TestMethod]
    public void ChordAlternationCostsMoreThanTheSingleNotes()
    {
        Assert.IsGreaterThan(TransitionCost.Between(G, R, P), TransitionCost.Between(G | R, R | Y, P));
        Assert.IsGreaterThan(TransitionCost.Between(G, Y, P), TransitionCost.Between(G | R, Y | B, P));
    }

    [TestMethod]
    public void RepeatingANoteCostsNothingAndTheFirstPaysOnlyItsShape()
    {
        Assert.AreEqual(0, TransitionCost.Between(G | Y, G | Y, P));
        Assert.AreEqual(0, TransitionCost.Between(null, R, P));

        // Green and yellow: two notes and one gap.
        Assert.AreEqual((2 * P.ChordNoteCost) + P.ChordGapCost, TransitionCost.Between(null, G | Y, P), 1e-9);
    }

    [TestMethod]
    public void WideChordsPayForTheirStretch()
    {
        // Green and orange span five lanes: three gaps, and two past three.
        Assert.AreEqual(5, TransitionCost.Span(G | O));
        Assert.AreEqual((2 * P.ChordNoteCost) + (3 * P.ChordGapCost) + (2 * P.ChordStretchCost), TransitionCost.Shape(G | O, P), 1e-9);
        Assert.AreEqual(0, TransitionCost.Span(Open));
        Assert.AreEqual(1, TransitionCost.Span(B));
    }
}
