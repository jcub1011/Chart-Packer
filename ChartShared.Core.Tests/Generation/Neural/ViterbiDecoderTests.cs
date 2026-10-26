using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

/// <summary>Viterbi decoding against brute force on small cases, plus the hard limits (§3.5).</summary>
[TestClass]
public sealed class ViterbiDecoderTests
{
    private const int Vocab = 3;

    private static ViterbiDecoder.DecodeConstraints Constraints(
        float[,] emissions, int n, long floor = 0, bool sp = false, string riffKey = "")
    {
        var beats = new List<BeatFrame>();
        var candidates = new List<HashSet<int>>();
        for (int i = 0; i < n; i++)
        {
            beats.Add(new BeatFrame(i * 100, 0, i % 4, 1, [0], [i * 100], 3, false, false, false, false, false, riffKey + i, 0));
            candidates.Add([0, 1, 2]);
        }

        return new ViterbiDecoder.DecodeConstraints(floor, sp, 0, candidates, beats);
    }

    private static (float[] Trans, float[] Start, float[] End) Weights(Random random)
    {
        float Next() => (float)(random.NextDouble() * 2 - 1);
        return (
            [.. Enumerable.Range(0, Vocab * Vocab).Select(_ => Next())],
            [.. Enumerable.Range(0, Vocab).Select(_ => Next())],
            [.. Enumerable.Range(0, Vocab).Select(_ => Next())]);
    }

    private static double BruteForceBest(float[,] emissions, float[] trans, float[] start, float[] end, int n)
    {
        double best = double.NegativeInfinity;
        var choice = new int[n];
        void Recurse(int i, double score)
        {
            if (i == n)
            {
                best = Math.Max(best, score + end[choice[n - 1]]);
                return;
            }

            for (int v = 0; v < Vocab; v++)
            {
                choice[i] = v;
                double s = score + emissions[i, v] + (i == 0 ? start[v] : trans[choice[i - 1] * Vocab + v]);
                Recurse(i + 1, s);
            }
        }

        Recurse(0, 0);
        return best;
    }

    private static double ScoreOf(int[] choice, float[,] emissions, float[] trans, float[] start, float[] end)
    {
        double score = start[choice[0]] + emissions[0, choice[0]];
        for (int i = 1; i < choice.Length; i++)
            score += trans[choice[i - 1] * Vocab + choice[i]] + emissions[i, choice[i]];
        return score + end[choice[^1]];
    }

    [TestMethod]
    public void DecodeMatchesBruteForce()
    {
        var random = new Random(11);
        for (int trial = 0; trial < 20; trial++)
        {
            int n = 2 + trial % 4;
            var emissions = new float[n, Vocab];
            for (int i = 0; i < n; i++)
                for (int v = 0; v < Vocab; v++)
                    emissions[i, v] = (float)(random.NextDouble() * 4 - 2);
            var (trans, start, end) = Weights(random);
            var constraints = Constraints(emissions, n);

            int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, constraints);

            Assert.AreEqual(BruteForceBest(emissions, trans, start, end, n), ScoreOf(choice, emissions, trans, start, end), 1e-4, $"Trial {trial}.");
        }
    }

    [TestMethod]
    public void MinGapFloorDropsTheWeakerOfAClosePair()
    {
        // Beat 0 keeps an onset at tick 0, beat 1 at tick 100; floor 480 drops beat 1's onset.
        // Give beat 1 a strong emission so the decoder wants it, then check repair drops it.
        var emissions = new float[2, Vocab];
        emissions[0, 1] = 5;
        emissions[1, 1] = 5;
        var beats = new List<BeatFrame>
        {
            new(0, 0, 0, 1, [0], [0], 3, false, false, false, false, false, "a", 0),
            new(100, 0, 1, 1, [0], [100], 3, false, false, false, false, false, "b", 0),
        };
        List<HashSet<int>> candidates = [[0, 1, 2], [0, 1, 2]];
        var constraints = new ViterbiDecoder.DecodeConstraints(480, false, 0, candidates, beats);
        var (trans, start, end) = Weights(new Random(3));

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, constraints);
        var kept = beats.Select((b, i) => (b, i)).Where(t => choice[t.i] != BeatPattern.EmptyId)
            .Select(t => t.b.SourceTicks[0]).Order().ToList();

        for (int i = 1; i < kept.Count; i++)
            Assert.IsGreaterThanOrEqualTo(480L, kept[i] - kept[i - 1], "Kept onsets honor the floor.");
    }

    [TestMethod]
    public void RiffRepeatsTakeTheSamePattern()
    {
        var emissions = new float[4, Vocab];
        var random = new Random(5);
        for (int i = 0; i < 4; i++)
            for (int v = 0; v < Vocab; v++)
                emissions[i, v] = (float)(random.NextDouble() * 2 - 1);
        var beats = new List<BeatFrame>();
        for (int i = 0; i < 4; i++)
            beats.Add(new BeatFrame(i * 100, 0, 0, 1, [0], [i * 100], 3, false, false, false, false, false, "same-riff", 0));
        List<HashSet<int>> candidates = [[0, 1, 2], [0, 1, 2], [0, 1, 2], [0, 1, 2]];
        var constraints = new ViterbiDecoder.DecodeConstraints(0, false, 0, candidates, beats);
        var (trans, start, end) = Weights(random);

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, constraints);

        Assert.AreEqual(choice[0], choice[1]);
        Assert.AreEqual(choice[0], choice[2]);
        Assert.AreEqual(choice[0], choice[3]);
    }

    [TestMethod]
    public void StarPowerPhrasesAreNeverLeftEmpty()
    {
        var emissions = new float[1, Vocab];
        emissions[0, 0] = 10; // strongly prefers empty
        var beats = new List<BeatFrame>
        {
            new(0, 0, 0, 1, [0], [0], 3, false, false, false, true, false, "a", 0),
        };
        List<HashSet<int>> candidates = [[0, 1]];
        var constraints = new ViterbiDecoder.DecodeConstraints(0, true, 0, candidates, beats);
        var (trans, start, end) = Weights(new Random(9));

        int[] choice = ViterbiDecoder.Decode(emissions, trans, start, end, constraints);

        Assert.AreNotEqual(BeatPattern.EmptyId, choice[0]);
    }
}
