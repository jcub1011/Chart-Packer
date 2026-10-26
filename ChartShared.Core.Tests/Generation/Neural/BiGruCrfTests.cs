using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BiGruCrfTests
{
    private static (NeuralPack Pack, BiGruCrf Model) TinyModel()
    {
        int vocab = BeatPattern.Vocabulary.Length;
        var manifest = new PackManifest(
            NeuralPack.FormatVersion,
            BiGruCrf.ArchitectureName,
            BeatPipeline.BeatSchemaVersion,
            "test", "tests", "Test pack.",
            [new PackStyle(0, "Any", "Blend.")],
            ["Guitar"],
            ["Expert->Easy"],
            new PackInputs(true, true, 4, 2, ["Easy"], ["Any"]),
            new Dictionary<string, long> { ["Easy"] = 12 },
            new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0] },
            [.. BeatPattern.Vocabulary],
            new PackTrainingSummary(1, 1, "tests", "2026-10-26", "tests"),
            "test");
        var random = new Random(4);
        float Next() => (float)(random.NextDouble() - 0.5);
        var weights = new PackWeights
        {
            SourceEmbedding = [.. Enumerable.Range(0, vocab * 2).Select(_ => Next())],
            StyleEmbedding = [.. Enumerable.Range(0, 2).Select(_ => Next())],
            TargetEmbedding = [.. Enumerable.Range(0, 2).Select(_ => Next())],
            Forward = Gru(15, 4, random),
            Backward = Gru(15, 4, random),
            OutputWeight = [.. Enumerable.Range(0, 8 * vocab).Select(_ => Next())],
            OutputBias = new float[vocab],
            Transition = new float[vocab * vocab],
            StartTransition = new float[vocab],
            EndTransition = new float[vocab],
        };
        var pack = new NeuralPack { Manifest = manifest, Weights = weights };
        return (pack, new BiGruCrf(pack));
    }

    private static GruWeights Gru(int input, int hidden, Random random)
    {
        float Next() => (float)(random.NextDouble() - 0.5);
        float[] W() => [.. Enumerable.Range(0, hidden * input).Select(_ => Next())];
        float[] U() => [.. Enumerable.Range(0, hidden * hidden).Select(_ => Next())];
        return new GruWeights
        {
            Wz = W(), Wr = W(), Wh = W(),
            Uz = U(), Ur = U(), Uh = U(),
            BzIh = new float[hidden], BzHh = new float[hidden], BrIh = new float[hidden], BrHh = new float[hidden], BnIh = new float[hidden], BnHh = new float[hidden],
        };
    }

    private static List<BeatFrame> Beats(int count)
    {
        var beats = new List<BeatFrame>();
        for (int i = 0; i < count; i++)
            beats.Add(new BeatFrame(i * 480, 0, i % 4, 3, [0, 24], [i * 480, i * 480 + 240], 3, false, false, false, false, false, "k" + i, 0));
        return beats;
    }

    [TestMethod]
    public void EmissionsAreDeterministic()
    {
        var (_, model) = TinyModel();
        var beats = Beats(8);
        var conditioning = new RunConditioning(ChartDifficulty.Easy, ChartDifficulty.Expert, ChartInstrumentKind.FiveFret, 0, 3);
        var sets = beats.Select(_ => new HashSet<int> { 0, 1, 2, 3 }).ToList();

        var first = model.Emissions(beats, conditioning, sets);
        var second = model.Emissions(beats, conditioning, sets);

        for (int i = 0; i < beats.Count; i++)
            for (int v = 0; v < 4; v++)
                Assert.AreEqual(first[i, v], second[i, v], "Same input and pack always give the same draft (§3.3).");
    }

    [TestMethod]
    public void MaskedCandidatesScoreNegativeInfinity()
    {
        var (_, model) = TinyModel();
        var beats = Beats(2);
        var conditioning = new RunConditioning(ChartDifficulty.Easy, ChartDifficulty.Expert, ChartInstrumentKind.FiveFret, 0, 3);
        List<HashSet<int>> sets = [[1], [0, 1, 2, 3]];

        var emissions = model.Emissions(beats, conditioning, sets);

        Assert.IsTrue(float.IsNegativeInfinity(emissions[0, 0]));
        Assert.IsFalse(float.IsNegativeInfinity(emissions[0, 1]));
        Assert.IsFalse(float.IsNegativeInfinity(emissions[1, 2]));
    }

    [TestMethod]
    public void AnyLengthWorksFromOneMeasureToASong()
    {
        var (_, model) = TinyModel();
        var conditioning = new RunConditioning(ChartDifficulty.Easy, ChartDifficulty.Expert, ChartInstrumentKind.FiveFret, 0, 3);

        foreach (int count in new[] { 1, 4, 600 })
        {
            var beats = Beats(count);
            var sets = beats.Select(_ => new HashSet<int> { 0, 1 }).ToList();

            var emissions = model.Emissions(beats, conditioning, sets);

            Assert.AreEqual(count, emissions.GetLength(0));
        }
    }
}
