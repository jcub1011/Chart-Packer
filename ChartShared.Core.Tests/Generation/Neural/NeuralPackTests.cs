using System.Text;
using System.Text.Json.Nodes;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class NeuralPackTests
{
    private static NeuralPack Pack()
    {
        var manifest = NeuralTestPacks.Manifest();
        return NeuralTestPacks.Pack(manifest, NeuralTestPacks.RandomWeights(manifest, 2));
    }

    // The pack's JSON with one edit made to it. The shape is checked before the checksum, so the edit is what's refused.
    private static byte[] Edited(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(Pack().ToBytes())!.AsObject();
        edit(json);
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    [TestMethod]
    public void PackRoundTripsThroughBytes()
    {
        var pack = Pack();

        var loaded = NeuralPack.LoadFromBytes(pack.ToBytes(), "test");

        Assert.AreEqual(pack.Manifest.Name, loaded.Manifest.Name);
        Assert.AreEqual(pack.Weights.ParameterCount, loaded.Weights.ParameterCount);
        CollectionAssert.AreEqual(pack.Weights.PairEmbedding, loaded.Weights.PairEmbedding);
    }

    [TestMethod]
    public void DamagedPacksAreRefused()
    {
        byte[] good = Pack().ToBytes();

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
        var pack = Pack();
        pack = new NeuralPack { Manifest = pack.Manifest with { BeatSchemaVersion = BeatPipeline.BeatSchemaVersion + 1 }, Weights = pack.Weights };

        var ex = Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(pack.ToBytes(), "wrong"));
        StringAssert.Contains(ex.Message, "beat schema");
    }

    [TestMethod]
    public void WrongDimensionsAreRefused()
    {
        // One weight short: hand-edit the JSON so serialization isn't involved.
        string json = Encoding.UTF8.GetString(Pack().ToBytes());
        string marker = "],\"OutputBias\"";
        int end = json.IndexOf(marker, StringComparison.Ordinal);
        int comma = json.LastIndexOf(',', end);
        string shortened = json[..comma] + json[end..];

        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(Encoding.UTF8.GetBytes(shortened), "short"));
    }

    [TestMethod]
    [DataRow("Inputs")]
    [DataRow("Instruments")]
    [DataRow("Pairs")]
    [DataRow("Styles")]
    [DataRow("Vocabulary")]
    [DataRow("MinGapTicks")]
    public void AManifestMissingAPartIsRefusedNotCrashedOn(string member)
    {
        byte[] bytes = Edited(json => json["Manifest"]![member] = null);

        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(bytes, "missing"));
    }

    [TestMethod]
    public void MissingWeightsAreRefusedNotCrashedOn()
    {
        byte[] bytes = Edited(json => json["Weights"]!["Forward"]!["Uz"] = null);

        Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(bytes, "missing"));
    }

    [TestMethod]
    public void AnotherVocabularyIsRefused()
    {
        // The decoder reads pattern ids as this app's vocabulary: a reordered one would keep the wrong onsets.
        byte[] bytes = Edited(json =>
        {
            var vocabulary = json["Manifest"]!["Vocabulary"]!.AsArray();
            var first = vocabulary[1]!.DeepClone();
            vocabulary[1] = vocabulary[2]!.DeepClone();
            vocabulary[2] = first;
        });

        var ex = Assert.ThrowsExactly<PackValidationException>(() => NeuralPack.LoadFromBytes(bytes, "reordered"));
        StringAssert.Contains(ex.Message, "vocabulary");
    }

    [TestMethod]
    public async Task TheCacheReadsAReplacedPackAgain()
    {
        var root = Directory.CreateTempSubdirectory("chartpacker-neural-cache-");
        try
        {
            var store = new GeneratorModelStore(new GeneratorModelFolderFile(Path.Combine(root.FullName, "settings.json")), Path.Combine(root.FullName, "models"));
            var id = new GeneratorModelId(GeneratorType.NeuralDifficulty, "test");
            var cache = new NeuralPackCache();
            using (var stream = new MemoryStream(Pack().ToBytes()))
                await store.SaveAsync(id, stream, overwrite: true);

            var first = cache.Load(store, "test");
            Assert.AreSame(first.Pack, cache.Load(store, "test").Pack, "An unchanged pack is read once.");

            var manifest = NeuralTestPacks.Manifest() with { Description = "Replaced." };
            using (var stream = new MemoryStream(NeuralTestPacks.Pack(manifest, NeuralTestPacks.RandomWeights(manifest, 3)).ToBytes()))
                await store.SaveAsync(id, stream, overwrite: true);
            File.SetLastWriteTimeUtc(store.ResolvePath(id), DateTime.UtcNow.AddMinutes(1));

            Assert.AreEqual("Replaced.", cache.Load(store, "test").Pack.Manifest.Description);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
