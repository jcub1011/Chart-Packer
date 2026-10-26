using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BeatPatternTests
{
    private static int Id(string pattern) => Array.IndexOf(BeatPattern.Vocabulary, pattern);

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
    public void EverySubsetOfTheSixteenthsCanBeKept()
    {
        var candidates = BeatPattern.CandidatesFor([0, 12, 24, 36]);

        // All sixteen subsets, empty among them: "e a" and "1 e a" too.
        Assert.HasCount(16, candidates);
        Assert.IsTrue(candidates.Contains(Id("e a")));
        Assert.IsTrue(candidates.Contains(Id("1 e a")));
        Assert.IsTrue(candidates.Contains(Id("e & a")));
    }

    [TestMethod]
    public void AFullTripletKeepsItsDownbeat()
    {
        // The reference keeping all three triplet notes is labelled as all three, not "t T" without the downbeat.
        var candidates = BeatPattern.CandidatesFor([0, 16, 32]);

        Assert.AreEqual(Id("1 t T"), BeatPattern.NearestCandidate(candidates, [0, 16, 32]));
        Assert.AreEqual(Id("1 t"), BeatPattern.NearestCandidate(candidates, [0, 16]));
    }

    [TestMethod]
    public void OnsetsSnapToTheNearest48th()
    {
        // A note a tick early is on the grid, not a 48th before it; a note just before the next beat stays in this one.
        Assert.AreEqual(24, BeatPattern.OffsetOf(239, 480));
        Assert.AreEqual(24, BeatPattern.OffsetOf(241, 480));
        Assert.AreEqual(0, BeatPattern.OffsetOf(0, 480));
        Assert.AreEqual(47, BeatPattern.OffsetOf(479, 480));
        Assert.AreEqual(16, BeatPattern.OffsetOf(160, 480));
    }

    [TestMethod]
    public void MapToSourceKeepsOnlySourceOnsets()
    {
        // Pattern "1 & a" mapped onto a beat that only plays 1 &: the a is dropped.
        var mapped = BeatPattern.MapToSource([0, 24], Id("1 & a"));

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
