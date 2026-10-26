using System.Text;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class NeuralPackTests
{
    private const int Hidden = 4;
    private const int Embed = 2;

    private static int Vocab => BeatPattern.Vocabulary.Length;

    private static PackManifest Manifest() => new(
        NeuralPack.FormatVersion,
        BiGruCrf.ArchitectureName,
        BeatPipeline.BeatSchemaVersion,
        "test", "tests", "Test pack.",
        [new PackStyle(0, "Any", "Blend.")],
        ["Guitar"],
        ["Expert->Easy"],
        new PackInputs(true, true, Hidden, Embed, ["Easy"], ["Any"]),
        new Dictionary<string, long> { ["Easy"] = 12 }, new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0] },
        [.. BeatPattern.Vocabulary],
        new PackTrainingSummary(1, 1, "tests", "2026-10-26", "tests"),
        "test");

    private static PackWeights Weights(Random? random = null)
    {
        random ??= new Random(2);
        float Next() => (float)(random.NextDouble() - 0.5);
        return new PackWeights
        {
            SourceEmbedding = [.. Enumerable.Range(0, Vocab * Embed).Select(_ => Next())],
            StyleEmbedding = [.. Enumerable.Range(0, Embed).Select(_ => Next())],
            TargetEmbedding = [.. Enumerable.Range(0, Embed).Select(_ => Next())],
            Forward = Gru(15, Hidden, random),
            Backward = Gru(15, Hidden, random),
            OutputWeight = [.. Enumerable.Range(0, 2 * Hidden * Vocab).Select(_ => Next())],
            OutputBias = new float[Vocab],
            Transition = new float[Vocab * Vocab],
            StartTransition = new float[Vocab],
            EndTransition = new float[Vocab],
        };
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

    [TestMethod]
    public void PackRoundTripsThroughBytes()
    {
        var pack = new NeuralPack { Manifest = Manifest(), Weights = Weights() };

        var loaded = NeuralPack.LoadFromBytes(pack.ToBytes(), "test");

        Assert.AreEqual(pack.Manifest.Name, loaded.Manifest.Name);
        Assert.AreEqual(pack.Weights.ParameterCount, loaded.Weights.ParameterCount);
        CollectionAssert.AreEqual(pack.Weights.OutputBias, loaded.Weights.OutputBias);
    }

    [TestMethod]
    public void DamagedPacksAreRefused()
    {
        byte[] good = new NeuralPack { Manifest = Manifest(), Weights = Weights() }.ToBytes();

        // Not JSON at all.
        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes("not a pack"u8.ToArray(), "bad"));

        // Flipped byte: the checksum no longer matches.
        byte[] flipped = (byte[])good.Clone();
        flipped[flipped.Length / 2] ^= 0xFF;
        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(flipped, "flipped"));
    }

    [TestMethod]
    public void WrongSchemaIsRefusedWithAnExplanation()
    {
        var pack = new NeuralPack
        {
            Manifest = Manifest() with { BeatSchemaVersion = BeatPipeline.BeatSchemaVersion + 1 },
            Weights = Weights(),
        };

        var ex = Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(pack.ToBytes(), "wrong"));
        StringAssert.Contains(ex.Message, "beat schema");
    }

    [TestMethod]
    public void WrongDimensionsAreRefused()
    {
        var weights = Weights();
        var pack = new NeuralPack { Manifest = Manifest(), Weights = weights };
        // One weight short: hand-edit the JSON so serialization isn't involved.
        string json = Encoding.UTF8.GetString(pack.ToBytes());
        string marker = "],\"OutputBias\"";
        int end = json.IndexOf(marker, StringComparison.Ordinal);
        int comma = json.LastIndexOf(',', end);
        string shortened = json[..comma] + json[end..];

        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(Encoding.UTF8.GetBytes(shortened), "short"));
    }
}
