using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

/// <summary>Synthetic neural packs and beats for the neural tests: never trained, never from a real chart.</summary>
internal static class NeuralTestPacks
{
    public static PackManifest Manifest(
        int hidden = 4,
        int embed = 2,
        string[]? styles = null,
        string[]? instruments = null,
        string[]? pairs = null,
        bool useRank = true)
    {
        styles ??= ["Any"];
        string[] targets = ["Easy", "Medium", "Hard"];
        return new PackManifest(
            NeuralPack.FormatVersion,
            BiGruCrf.ArchitectureName,
            BeatPipeline.BeatSchemaVersion,
            "test", "tests", "Test pack.",
            [.. styles.Select((s, i) => new PackStyle(i, s, s))],
            [.. instruments ?? ["Guitar"]],
            [.. pairs ?? ["Expert->Easy", "Expert->Medium", "Expert->Hard"]],
            new PackInputs(useRank, true, hidden, embed),
            targets.ToDictionary(static t => t, static _ => 12L),
            targets.ToDictionary(static t => t, static _ => new[] { 0.5, 2.0 }),
            [.. BeatPattern.Vocabulary],
            new PackTrainingSummary(1, 1, "tests", "2026-10-26", "tests"),
            "test");
    }

    /// <summary>Weights of the manifest's shapes, random from <paramref name="seed"/>.</summary>
    public static PackWeights RandomWeights(PackManifest manifest, int seed)
    {
        var random = new Random(seed);
        float Next() => (float)(random.NextDouble() - 0.5);
        return Weights(manifest, Next, outputBias: null);
    }

    /// <summary>All-zero weights but the output bias: every beat scores each pattern by <paramref name="outputBias"/> alone.</summary>
    public static PackWeights BiasWeights(PackManifest manifest, float[] outputBias) =>
        Weights(manifest, static () => 0f, outputBias);

    public static NeuralPack Pack(PackManifest manifest, PackWeights weights) => new() { Manifest = manifest, Weights = weights };

    /// <summary>A beat for the decoder: its onsets at their 48ths of a beat <paramref name="length"/> ticks long.</summary>
    public static BeatFrame Beat(long start, int[] offsets, string riffKey, int starPower = -1, int solo = -1, long length = 480) =>
        new(start, 0, 0, offsets.Length == 0 ? BeatPattern.EmptyId : BeatPattern.EncodeSource(offsets), offsets,
            [.. offsets.Select(o => start + (o * length / BeatPattern.UnitsPerBeat))],
            3, false, false, false, starPower, solo, riffKey, 0, 0.5, false, false);

    private static PackWeights Weights(PackManifest manifest, Func<float> next, float[]? outputBias)
    {
        int vocab = manifest.Vocabulary.Count;
        int hidden = manifest.Inputs.HiddenSize;
        int embed = manifest.Inputs.EmbeddingSize;
        int input = BiGruCrf.FeatureSize(manifest);
        float[] Values(int count) => [.. Enumerable.Range(0, count).Select(_ => next())];
        GruWeights Gru() => new()
        {
            Wz = Values(hidden * input), Wr = Values(hidden * input), Wh = Values(hidden * input),
            Uz = Values(hidden * hidden), Ur = Values(hidden * hidden), Uh = Values(hidden * hidden),
            BzIh = new float[hidden], BzHh = new float[hidden], BrIh = new float[hidden], BrHh = new float[hidden], BnIh = new float[hidden], BnHh = new float[hidden],
        };
        return new PackWeights
        {
            SourceEmbedding = Values(vocab * embed),
            StyleEmbedding = Values(manifest.Styles.Count * embed),
            PairEmbedding = Values(manifest.Pairs.Count * embed),
            InstrumentEmbedding = Values(manifest.Instruments.Count * embed),
            Forward = Gru(),
            Backward = Gru(),
            OutputWeight = Values(2 * hidden * vocab),
            OutputBias = outputBias ?? new float[vocab],
            Transition = new float[vocab * vocab],
            StartTransition = new float[vocab],
            EndTransition = new float[vocab],
        };
    }
}
