using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>
/// Self-test (spec §3.3 parity, §9): the trainer's TorchSharp model and the apps' managed C# inference
/// must agree within tolerance on the same inputs, after export into a pack — for sequences of different lengths batched
/// together, as training pads and packs them. Also smokes one training step (loss must fall). Not shipped with the apps.
/// </summary>
internal static class SelfTest
{
    /// <summary>The parity tolerance of spec §3.3: trainer vs app inference on the same inputs.</summary>
    private const double ParityTolerance = 1e-4;

    public static int Run(string[] args)
    {
        int vocab = BeatPattern.Vocabulary.Length;
        const int hidden = 8, embed = 4;
        string[] styles = ["Any", "S"];
        string[] pairs = ["Expert->Easy", "Hard->Medium"];
        string[] instruments = ["Guitar", "Bass"];
        var random = new Random(1234);
        using var trainer = new NeuralTrainer(vocab, styles.Length, pairs.Length, instruments.Length, hidden, embed, seed: 7, learningRate: 1e-2);
        Console.WriteLine($"Device: {trainer.Device}.");

        // Two sequences of different lengths, with different conditioning: exercises padded, packed batching.
        var first = Sequence(random, 7, style: 1, pair: 0, instrument: 1, rank: 3.4);
        var second = Sequence(random, 4, style: 0, pair: 1, instrument: 0, rank: null);
        var batch = new List<BatchItem> { first.Item, second.Item };

        double before = trainer.EvalBatch(batch);
        trainer.TrainBatch(batch);
        double after = trainer.EvalBatch(batch);
        Console.WriteLine($"Loss: {before:F6} -> {after:F6} after one step.");
        if (!(after < before))
        {
            Console.Error.WriteLine("SELF-TEST FAILED: one Adam step did not reduce the loss.");
            return 1;
        }

        // Parity: export the trained weights through the pack format and compare TorchSharp vs managed C# emissions.
        var manifest = new PackManifest(
            NeuralPack.FormatVersion,
            BiGruCrf.ArchitectureName,
            BeatPipeline.BeatSchemaVersion,
            "parity", "selftest", "Parity fixture.",
            [new PackStyle(0, "Any", "d"), new PackStyle(1, "S", "d")],
            [.. instruments],
            [.. pairs],
            new PackInputs(true, true, hidden, embed),
            new Dictionary<string, long> { ["Easy"] = 12 },
            new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0] },
            [.. BeatPattern.Vocabulary],
            new PackTrainingSummary(0, 0, "d", "d", "d", null),
            "test");
        NeuralPack pack;
        try
        {
            pack = NeuralPack.LoadFromBytes(new NeuralPack { Manifest = manifest, Weights = trainer.Export() }.ToBytes(), "parity");
        }
        catch (PackValidationException ex)
        {
            Console.Error.WriteLine($"SELF-TEST FAILED: the exported pack doesn't load ({ex.Message}).");
            return 1;
        }

        var torchEmissions = trainer.ComputeEmissions(batch);
        var app = new BiGruCrf(pack);
        double worst = 0;
        foreach (var (sequence, index) in new[] { first, second }.Select((s, i) => (s, i)))
        {
            var sets = sequence.Item.Candidates.Select(static c => c.ToHashSet()).ToList();
            var appEmissions = app.Emissions(sequence.Item.Source, sequence.Scalars, sequence.Item.StyleId, sequence.Item.PairIndex, sequence.Item.InstrumentIndex, sequence.Rank, sets);
            for (int i = 0; i < sequence.Item.Length; i++)
            {
                foreach (int v in sets[i])
                    worst = Math.Max(worst, Math.Abs(torchEmissions[index][i, v] - appEmissions[i, v]));
            }
        }

        Console.WriteLine($"Parity worst abs diff: {worst:E3} (tolerance {ParityTolerance:E0}).");
        if (worst > ParityTolerance || double.IsNaN(worst))
        {
            Console.Error.WriteLine("SELF-TEST FAILED: trainer and app inference disagree.");
            return 1;
        }

        Console.WriteLine("SELF-TEST PASSED.");
        return 0;
    }

    // A random sequence: its beats' scalars and their features, every beat with at least two candidates.
    private static (BatchItem Item, BeatScalars[] Scalars, double? Rank) Sequence(Random random, int n, int style, int pair, int instrument, double? rank)
    {
        int vocab = BeatPattern.Vocabulary.Length;
        var source = new int[n];
        var candidates = new int[n][];
        var labels = new int[n];
        var scalars = new BeatScalars[n];
        var features = new float[n * BeatFeatures.Count];
        for (int i = 0; i < n; i++)
        {
            source[i] = random.Next(vocab);
            int a = random.Next(vocab);
            int b = (a + 1 + random.Next(vocab - 1)) % vocab;
            candidates[i] = [a, b];
            labels[i] = random.Next(2) == 0 ? a : b;
            scalars[i] = new BeatScalars(
                random.Next(4), random.Next(8), random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0,
                random.Next(2) == 0, random.Next(2) == 0, random.Next(5), random.Next(6), random.Next(2) == 0,
                random.Next(2) == 0, (float)(0.2 + random.NextDouble()));
            BeatFeatures.Write(scalars[i], rank, true, features.AsSpan(i * BeatFeatures.Count, BeatFeatures.Count));
        }

        return (new BatchItem(source, candidates, features, labels, style, pair, instrument), scalars, rank);
    }
}
