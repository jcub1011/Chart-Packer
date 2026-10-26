using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

/// <summary>Viterbi decoding against brute force on small cases, plus the hard limits (§3.5).</summary>
[TestClass]
public sealed class ViterbiDecoderTests
{
    private static int Vocab => BeatPattern.Vocabulary.Length;

    // Beats of "1 &" (candidates -, 1, &, 1 &), each its own riff unless riffKey is given.
    private static (List<BeatFrame> Beats, List<HashSet<int>> Candidates) Beats(int n, string? riffKey = null)
    {
        var beats = Enumerable.Range(0, n).Select(i => NeuralTestPacks.Beat(i * 480L, [0, 24], riffKey ?? "k" + i)).ToList();
        return (beats, [.. beats.Select(static b => BeatPattern.CandidatesFor(b.SourceOffsets48).ToHashSet())]);
    }

    private static float[,] Emissions(Random random, List<HashSet<int>> candidates)
    {
        var emissions = new float[candidates.Count, Vocab];
        for (int i = 0; i < candidates.Count; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = candidates[i].Contains(v) ? (float)(random.NextDouble() * 4 - 2) : float.NegativeInfinity;
        return emissions;
    }

    private static (float[] Trans, float[] Start, float[] End) Weights(Random random)
    {
        float Next() => (float)(random.NextDouble() * 2 - 1);
        return (
            [.. Enumerable.Range(0, Vocab * Vocab).Select(_ => Next())],
            [.. Enumerable.Range(0, Vocab).Select(_ => Next())],
            [.. Enumerable.Range(0, Vocab).Select(_ => Next())]);
    }

    private static double BruteForceBest(float[,] emissions, List<HashSet<int>> candidates, float[] trans, float[] start, float[] end)
    {
        int n = candidates.Count;
        double best = double.NegativeInfinity;
        var choice = new int[n];
        void Recurse(int i, double score)
        {
            if (i == n)
            {
                best = Math.Max(best, score + end[choice[n - 1]]);
                return;
            }

            foreach (int v in candidates[i])
            {
                choice[i] = v;
                Recurse(i + 1, score + emissions[i, v] + (i == 0 ? start[v] : trans[(choice[i - 1] * Vocab) + v]));
            }
        }

        Recurse(0, 0);
        return best;
    }

    private static double ScoreOf(int[] choice, float[,] emissions, float[] trans, float[] start, float[] end)
    {
        double score = start[choice[0]] + emissions[0, choice[0]];
        for (int i = 1; i < choice.Length; i++)
            score += trans[(choice[i - 1] * Vocab) + choice[i]] + emissions[i, choice[i]];
        return score + end[choice[^1]];
    }

    private static List<long> Kept(List<BeatFrame> beats, int[] choice) =>
        [.. beats.SelectMany((b, i) => ViterbiDecoder.KeptTicks(b, choice[i])).Order()];

    [TestMethod]
    public void DecodeMatchesBruteForce()
    {
        var random = new Random(11);
        for (int trial = 0; trial < 20; trial++)
        {
            int n = 2 + (trial % 4);
            var (beats, candidates) = Beats(n);
            var emissions = Emissions(random, candidates);
            var (trans, start, end) = Weights(random);

            int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, new ViterbiDecoder.DecodeConstraints(0, false, 0, candidates, beats));

            Assert.AreEqual(BruteForceBest(emissions, candidates, trans, start, end), ScoreOf(choice, emissions, trans, start, end), 1e-4, $"Trial {trial}.");
        }
    }

    [TestMethod]
    public void MinGapFloorDropsTheLaterOfAClosePair()
    {
        // Every beat wants "1 &"; a floor of a beat keeps one onset a beat.
        var (beats, candidates) = Beats(4);
        var emissions = new float[4, Vocab];
        for (int i = 0; i < 4; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = !candidates[i].Contains(v) ? float.NegativeInfinity : v == 3 ? 5 : 0;
        var (trans, start, end) = Weights(new Random(3));

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, new ViterbiDecoder.DecodeConstraints(480, false, 0, candidates, beats));
        var kept = Kept(beats, choice);

        Assert.IsNotEmpty(kept);
        for (int i = 1; i < kept.Count; i++)
            Assert.IsGreaterThanOrEqualTo(480L, kept[i] - kept[i - 1], "Kept onsets honor the floor.");
    }

    [TestMethod]
    public void TheFloorHoldsAgainstTheTargetOutsideTheRange()
    {
        // Decoding beats 1–2 only (ticks 480, 720, 960, 1200), with the target keeping tick 400 before them and 1440 after.
        var (beats, candidates) = Beats(4);
        var emissions = new float[4, Vocab];
        for (int i = 0; i < 4; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = !candidates[i].Contains(v) ? float.NegativeInfinity : v == 3 ? 5 : 0;
        var (trans, start, end) = Weights(new Random(4));
        var constraints = new ViterbiDecoder.DecodeConstraints(240, false, 0, candidates, beats, (480, 1439), [400, 1440]);

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, constraints);
        var kept = Kept(beats, choice);

        // Nothing outside the range; 480 is closer than the floor to the target's 400; 1200 is a floor from 1440.
        CollectionAssert.AreEqual(new long[] { 720, 960, 1200 }, kept);
    }

    [TestMethod]
    public void RiffRepeatsTakeTheSamePattern()
    {
        var random = new Random(5);
        var (beats, candidates) = Beats(4, riffKey: "same-riff");
        var emissions = Emissions(random, candidates);
        var (trans, start, end) = Weights(random);

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, new ViterbiDecoder.DecodeConstraints(0, false, 0, candidates, beats));

        Assert.AreEqual(choice[0], choice[1]);
        Assert.AreEqual(choice[0], choice[2]);
        Assert.AreEqual(choice[0], choice[3]);
    }

    [TestMethod]
    public void StarPowerPhrasesKeepOneNoteNotEveryBeat()
    {
        // Four beats of one phrase that strongly prefer empty: the phrase keeps one onset, as Rules does (AC C9.3).
        var beats = Enumerable.Range(0, 4).Select(i => NeuralTestPacks.Beat(i * 480L, [0, 24], "k" + i, starPower: 0)).ToList();
        var candidates = beats.Select(static b => BeatPattern.CandidatesFor(b.SourceOffsets48).ToHashSet()).ToList();
        var emissions = new float[4, Vocab];
        for (int i = 0; i < 4; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = !candidates[i].Contains(v) ? float.NegativeInfinity : v == BeatPattern.EmptyId ? 10 : 0;
        var (trans, start, end) = Weights(new Random(9));

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, new ViterbiDecoder.DecodeConstraints(0, true, 0, candidates, beats));

        Assert.HasCount(1, Kept(beats, choice));
    }

    [TestMethod]
    public void TheDensityBiasLeansDenser()
    {
        var (beats, candidates) = Beats(4);
        var emissions = new float[4, Vocab];
        for (int i = 0; i < 4; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = !candidates[i].Contains(v) ? float.NegativeInfinity : v == 1 ? 1 : 0;
        var zero = new float[Vocab * Vocab];
        var none = new float[Vocab];

        int sparse = Kept(beats, ViterbiDecoder.Decode(emissions, zero, none, none, new ViterbiDecoder.DecodeConstraints(0, false, 0, candidates, beats))).Count;
        int dense = Kept(beats, ViterbiDecoder.Decode(emissions, zero, none, none, new ViterbiDecoder.DecodeConstraints(0, false, 2, candidates, beats))).Count;

        Assert.IsGreaterThan(sparse, dense);
    }
}
