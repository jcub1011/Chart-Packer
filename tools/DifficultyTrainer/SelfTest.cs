using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>
/// Self-test (spec §3.3 parity, §9): the trainer's TorchSharp model and the apps' managed C#
/// inference must agree within tolerance on the same inputs, after export into a pack.
/// Also smokes one training step (loss must fall). Not shipped with the apps.
/// </summary>
internal static class SelfTest
{
    /// <summary>The parity tolerance of spec §3.3: trainer vs app inference on the same inputs.</summary>
    private const double ParityTolerance = 1e-4;

    public static int Run(string[] args)
    {
        const int vocab = 4, hidden = 8, embed = 4, styles = 2, targets = 2, n = 6;
        var random = new Random(1234);
        using var trainer = new NeuralTrainer(vocab, styles, targets, hidden, embed, seed: 7);
        Console.WriteLine($"Device: {trainer.Device}.");

        // One random sequence; every beat keeps two candidates so the CRF has alternatives.
        var source = new int[n];
        var candidates = new int[n][];
        var scalars = new sbyte[n * 8];
        var labels = new int[n];
        for (int i = 0; i < n; i++)
        {
            source[i] = random.Next(vocab);
            int a = random.Next(vocab);
            int b = (a + 1 + random.Next(vocab - 1)) % vocab;
            candidates[i] = [a, b];
            labels[i] = random.Next(2) == 0 ? a : b;
            for (int k = 0; k < 8; k++)
                scalars[i * 8 + k] = (sbyte)random.Next(0, 4);
        }

        // The loop learns: one Adam step must reduce the loss on its own batch.
        // Two sequences of different lengths: exercises padded batching, not just batch 1.
        var item = new BatchItem(source, candidates, scalars, labels, 1, 0, 3);
        var scalars2 = new sbyte[4 * 8];
        Array.Copy(scalars, scalars2, scalars2.Length);
        var item2 = new BatchItem(source[..4], [.. candidates.Take(4)], scalars2, labels[..4], 0, 1, 0);
        var batch = new List<BatchItem> { item, item2 };
        double before = trainer.EvalBatch(batch);
        trainer.TrainBatch(batch);
        double after = trainer.EvalBatch(batch);
        Console.WriteLine($"Loss: {before:F6} -> {after:F6} after one step.");
        if (!(after < before))
        {
            Console.Error.WriteLine("SELF-TEST FAILED: one Adam step did not reduce the loss.");
            return 1;
        }

        // Parity: export the trained weights and compare TorchSharp vs managed C# emissions.
        var weights = trainer.Export();
        var manifest = new PackManifest(
            NeuralPack.FormatVersion,
            BiGruCrf.ArchitectureName,
            BeatPipeline.BeatSchemaVersion,
            "parity", "selftest", "Parity fixture.",
            [new PackStyle(0, "Any", "d"), new PackStyle(1, "S", "d")],
            ["Guitar"],
            ["Expert->Easy"],
            new PackInputs(true, true, hidden, embed, ["Easy", "Medium"], ["Any", "S"]),
            new Dictionary<string, long> { ["Easy"] = 12 },
            new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0] },
            ["-", "1", "&", "1 &"],
            new PackTrainingSummary(0, 0, "d", "d", "d", null),
            "test");
        var pack = new NeuralPack { Manifest = manifest, Weights = weights };

        var torchEmissions = trainer.ComputeEmissions(source, scalars, 1, 0, 3, useTier: true);
        var app = new BiGruCrf(pack);
        var beats = source.Select((s, i) => new BeatFrame(
            i, 0, scalars[i * 8], s,
            [0], [i], scalars[i * 8 + 1],
            scalars[i * 8 + 2] != 0, scalars[i * 8 + 3] != 0, scalars[i * 8 + 4] != 0,
            scalars[i * 8 + 5] != 0, scalars[i * 8 + 6] != 0, "", scalars[i * 8 + 7])).ToList();
        var conditioning = new RunConditioning(ChartDifficulty.Easy, ChartDifficulty.Expert, ChartInstrumentKind.FiveFret, 1, 3);
        var sets = candidates.Select(static c => c.ToHashSet()).ToList();
        var appEmissions = app.Emissions(beats, conditioning, sets);

        double worst = 0;
        for (int i = 0; i < n; i++)
            foreach (int v in sets[i])
                worst = Math.Max(worst, Math.Abs(torchEmissions[i, v] - appEmissions[i, v]));
        Console.WriteLine($"Parity worst abs diff: {worst:E3} (tolerance {ParityTolerance:E0}).");
        if (worst > ParityTolerance || double.IsNaN(worst))
        {
            Console.Error.WriteLine("SELF-TEST FAILED: trainer and app inference disagree.");
            return 1;
        }

        Console.WriteLine("SELF-TEST PASSED.");
        return 0;
    }
}
