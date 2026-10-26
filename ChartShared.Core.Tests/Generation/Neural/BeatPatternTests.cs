using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BeatPatternTests
{
    [TestMethod]
    public void EmptyIsAlwaysACandidate()
    {
        var candidates = BeatPattern.CandidatesFor([0, 24]);

        Assert.IsTrue(candidates.Contains(BeatPattern.EmptyId));
    }

    [TestMethod]
    public void DownbeatAndOffBeatEncode()
    {
        Assert.AreEqual(1, BeatPattern.EncodeSource([0]));
        Assert.AreEqual(2, BeatPattern.EncodeSource([24]));
        Assert.AreEqual(3, BeatPattern.EncodeSource([0, 24]));
        Assert.AreEqual(BeatPattern.EmptyId, BeatPattern.EncodeSource([]));
    }

    [TestMethod]
    public void CandidatesAreSubsetsOnSourceOnsets()
    {
        // Source plays 1 & : keepable shapes are -, 1, &, 1 &.
        var candidates = BeatPattern.CandidatesFor([0, 24]);

        CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, candidates.ToList());
    }

    [TestMethod]
    public void SixteenthsKeepTheirSlots()
    {
        var candidates = BeatPattern.CandidatesFor([0, 12, 24, 36]);

        Assert.IsTrue(candidates.Contains(12)); // 1 e & a
        Assert.IsTrue(candidates.Contains(6)); // 1 e
    }

    [TestMethod]
    public void MapToSourceKeepsOnlySourceOnsets()
    {
        // Pattern "1 & a" mapped onto a beat that only plays 1 &: the a is dropped.
        var mapped = BeatPattern.MapToSource([0, 24], 11);

        CollectionAssert.AreEquivalent(new[] { 0, 24 }, mapped.ToList());
    }

    [TestMethod]
    public void NearestCandidateStaysInsideTheCandidates()
    {
        // Exact subsets resolve exactly.
        Assert.AreEqual(3, BeatPattern.NearestCandidate([0, 1, 2, 3], [0, 24]));
        // Off-grid reference onsets fall back to the nearest supported shape.
        Assert.AreEqual(BeatPattern.EmptyId, BeatPattern.NearestCandidate([0], [6]));
        // Empty is always available.
        Assert.AreEqual(BeatPattern.EmptyId, BeatPattern.NearestCandidate([0, 1], []));
    }
}
