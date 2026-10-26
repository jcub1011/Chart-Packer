using System.Text.Json;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>Train neural (spec §6.1, §6.5): trains a BiGRU+CRF pack from a prepared set.</summary>
internal static class Train
{
    public static int Run(string[] args)
    {
        string input = Arg(args, "--in", "training-set.json");
        string output = Arg(args, "--out", "neural-pack.neuralpack");
        string name = Arg(args, "--name", "neural-default");
        int hidden = int.TryParse(Arg(args, "--hidden", "64"), out int h) ? h : 64;
        int embed = int.TryParse(Arg(args, "--embed", "16"), out int e) ? e : 16;
        int epochs = int.TryParse(Arg(args, "--epochs", "10"), out int ep) ? ep : 10;
        int seed = int.TryParse(Arg(args, "--seed", "42"), out int s) ? s : 42;
        double lr = double.TryParse(Arg(args, "--lr", "0.0003"), out double l) ? l : 0.0003;
        double anyShare = double.TryParse(Arg(args, "--any-share", "0.2"), out double a) ? a : 0.2;
        bool useTier = Arg(args, "--tier", "on") != "off";
        int patience = int.TryParse(Arg(args, "--patience", "3"), out int p) ? p : 3;
        int maxParts = int.TryParse(Arg(args, "--max-parts", "0"), out int mp) ? mp : 0;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Usage: train --in <training-set.json> --out <pack.neuralpack> [--hidden 64] [--embed 16] [--epochs 10] [--seed 42] [--max-parts N]");
            return 2;
        }

        TrainingSet set;
        using (var stream = File.OpenRead(input))
            set = JsonSerializer.Deserialize(stream, TrainingSetJsonContext.Default.TrainingSet)!;
        if (set is null)
        {
            Console.Error.WriteLine("Could not read the training set.");
            return 1;
        }

        var heldOut = new HashSet<string>(StringComparer.Ordinal);
        string heldOutPath = input + ".heldout";
        if (File.Exists(heldOutPath))
            heldOut = [.. File.ReadAllLines(heldOutPath).Where(static l => l.Length > 0)];

        var trainParts = set.Examples.Where(e => !heldOut.Contains(e.SongKey)).ToList();
        var heldOutParts = set.Examples.Where(e => heldOut.Contains(e.SongKey)).ToList();
        if (maxParts > 0)
        {
            trainParts = trainParts.Take(maxParts).ToList();
            heldOutParts = heldOutParts.Take(Math.Max(1, maxParts / 4)).ToList();
        }

        Console.WriteLine($"Training parts: {trainParts.Count}; held-out parts: {heldOutParts.Count}.");
        if (trainParts.Count == 0)
        {
            Console.Error.WriteLine("No training parts.");
            return 1;
        }

        var styles = set.Styles.Length > 0 ? set.Styles : ["Any"];
        var targets = set.Targets.Length > 0 ? set.Targets : ["Easy", "Medium", "Hard"];
        int vocab = BeatPattern.Vocabulary.Length;
        using var trainer = new NeuralTrainer(vocab, styles.Length, targets.Length, hidden, embed, seed)
        {
            LearningRate = lr,
            AnyStyleShare = anyShare,
        };

        var random = new Random(seed);
        double best = double.PositiveInfinity;
        int stale = 0;
        var bestWeights = trainer.Export();
        Console.WriteLine($"Device: {trainer.Device}.");

        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            // Bucket by length for padded batching; shuffled reproducibly from the seed (§6.5).
            var items = trainParts.Select(part => new BatchItem(
                part.SourcePatterns,
                [.. part.Candidates.Select(static c => c.ToArray())],
                part.Scalars,
                part.Labels,
                StyleIdOf(styles, part.Style, random, anyShare),
                TargetIndexOf(targets, part.Pair),
                BeatPipeline.TierBucketOf(part.Tier))).ToList();
            var batches = Batcher.Bucket(items, batchSize: 32, random);
            double total = 0;
            int count = 0;
            foreach (var batch in batches)
            {
                total += trainer.TrainBatch(batch) * batch.Count;
                count += batch.Count;
                if (count % 2000 == 0)
                    Console.WriteLine($"  epoch {epoch}: {count}/{items.Count} parts, NLL {total / count:F3}");
            }

            double heldOutLoss = HeldOutLoss(trainer, heldOutParts, styles, targets, useTier);
            Console.WriteLine($"Epoch {epoch}: train NLL {total / Math.Max(1, count):F3}, held-out NLL {heldOutLoss:F3}.");
            if (heldOutLoss < best - 1e-4)
            {
                best = heldOutLoss;
                bestWeights = trainer.Export();
                stale = 0;
            }
            else if (++stale >= patience)
            {
                Console.WriteLine($"Early stopping (held-out NLL stopped improving, §6.5). Best: {best:F3}.");
                break;
            }
        }

        // Measure hard-limit defaults + density spread from the training charts (§6.6).
        var gaps = MeasureGaps(trainParts);
        var manifest = new PackManifest(
            NeuralPack.FormatVersion,
            BiGruCrf.ArchitectureName,
            BeatPipeline.BeatSchemaVersion,
            name, "DifficultyTrainer", "BiGRU+CRF pack trained on official charts (spec §3.3).",
            [.. styles.Select((s, i) => new PackStyle(i, s, i == 0 ? "Blend of all styles (§3.3 Any)." : $"Trained style: {s}."))],
            ["Guitar", "Bass", "Keys", "Rhythm"],
            [.. set.Pairs],
            new PackInputs(useTier, true, hidden, embed, targets, styles),
            gaps,
            new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0], ["Medium"] = [0.5, 2.0], ["Hard"] = [0.5, 2.0] },
            [.. BeatPattern.Vocabulary],
            new PackTrainingSummary(
                trainParts.Select(static p => p.SongKey).Distinct().Count(),
                trainParts.Count,
                string.Join(",", styles.Skip(1).Take(5)),
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                "DifficultyTrainer 1.0",
                $"Held-out NLL {best:F3}."),
            "See pack repository license.",
            "");

        var pack = new NeuralPack { Manifest = manifest, Weights = bestWeights };
        File.WriteAllBytes(output, pack.ToBytes());
        Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024} KB, {bestWeights.ParameterCount} params).");
        return 0;
    }

    private static double HeldOutLoss(NeuralTrainer trainer, List<PreparedPart> parts, string[] styles, string[] targets, bool useTier)
    {
        if (parts.Count == 0)
            return double.PositiveInfinity;
        var items = parts.Take(500).Select(part => new BatchItem(
            part.SourcePatterns,
            [.. part.Candidates.Select(static c => c.ToArray())],
            part.Scalars,
            part.Labels,
            StyleIdOf(styles, part.Style, null, 0),
            TargetIndexOf(targets, part.Pair),
            BeatPipeline.TierBucketOf(part.Tier))).ToList();
        double total = 0;
        int count = 0;
        foreach (var batch in Batcher.Bucket(items, 32, new Random(0)))
        {
            total += trainer.EvalBatch(batch) * batch.Count;
            count += batch.Count;
        }

        return count == 0 ? double.PositiveInfinity : total / count;
    }

    private static int StyleIdOf(string[] styles, string style, Random? random, double anyShare)
    {
        if (random is not null && random.NextDouble() < anyShare)
            return 0; // Any-style training (§3.3).
        for (int i = 0; i < styles.Length; i++)
        {
            if (string.Equals(styles[i], style, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private static int TargetIndexOf(string[] targets, string pair)
    {
        string to = pair.Split("->").Last();
        for (int i = 0; i < targets.Length; i++)
        {
            if (string.Equals(targets[i], to, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private static Dictionary<string, long> MeasureGaps(List<PreparedPart> parts)
    {
        // Minimum gaps per target, measured from the training charts (§3.5): the 1st percentile of
        // kept-onset gaps, in 48ths of a beat (the app scales by the chart's beat length). A floor
        // below Rules' thinning grid, so closer pairs the official charts have still fit.
        var result = new Dictionary<string, long>();
        foreach (var target in new[] { "Easy", "Medium", "Hard" })
        {
            var gaps = new List<int>();
            foreach (var part in parts.Where(p => p.Pair.EndsWith("->" + target)))
            {
                int last = -1;
                for (int i = 0; i < part.Labels.Length; i++)
                {
                    if (part.Labels[i] != BeatPattern.EmptyId)
                    {
                        if (last >= 0)
                            gaps.Add(i - last);
                        last = i;
                    }
                }
            }

            gaps.Sort();
            long floor48 = gaps.Count == 0 ? 12 : Math.Max(6, (long)gaps[Math.Min(gaps.Count - 1, gaps.Count / 100)] * 48);
            result[target] = floor48;
        }

        return result;
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return fallback;
    }
}
