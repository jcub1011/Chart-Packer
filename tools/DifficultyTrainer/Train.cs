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
        int epochs = int.TryParse(Arg(args, "--epochs", "30"), out int ep) ? ep : 30;
        int seed = int.TryParse(Arg(args, "--seed", "42"), out int s) ? s : 42;
        double lr = double.TryParse(Arg(args, "--lr", "0.001"), out double l) ? l : 0.001;
        double anyShare = double.TryParse(Arg(args, "--any-share", "0.2"), out double a) ? a : 0.2;
        bool useRank = Arg(args, "--rank", "on") != "off";
        int patience = int.TryParse(Arg(args, "--patience", "3"), out int p) ? p : 3;
        int maxParts = int.TryParse(Arg(args, "--max-parts", "0"), out int mp) ? mp : 0;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("Usage: train --in <training-set.json> --out <pack.neuralpack> [--name N] [--hidden 64] [--embed 16] [--epochs 30] [--patience 3] [--lr 0.001] [--seed 42] [--any-share 0.2] [--rank on|off] [--max-parts N]");
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

        if (set.BeatSchemaVersion != BeatPipeline.BeatSchemaVersion)
        {
            Console.Error.WriteLine($"The training set is beat schema {set.BeatSchemaVersion}; this trainer is {BeatPipeline.BeatSchemaVersion}. Run prepare again.");
            return 1;
        }

        var heldOut = HeldOutKeys(input);
        var trainParts = set.Examples.Where(x => !heldOut.Contains(x.SongKey)).ToList();
        var heldOutParts = set.Examples.Where(x => heldOut.Contains(x.SongKey)).ToList();
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
        string[] pairs = set.Pairs;
        string[] instruments = set.Instruments;
        int vocab = BeatPattern.Vocabulary.Length;
        using var trainer = new NeuralTrainer(vocab, styles.Length, pairs.Length, instruments.Length, hidden, embed, seed, lr);

        // The items once: only the style changes between epochs (Any on a share of them, §3.3).
        var trainItems = trainParts.Select(part => ItemOf(part, styles, pairs, instruments, useRank)).ToList();
        var heldOutItems = heldOutParts.Take(500).Select(part => ItemOf(part, styles, pairs, instruments, useRank)).ToList();

        var random = new Random(seed);
        double best = double.PositiveInfinity;
        int stale = 0;
        var bestWeights = trainer.Export();
        Console.WriteLine($"Device: {trainer.Device}; learning rate {lr}; source rank {(useRank ? "on" : "off")}.");

        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            // Bucket by length for padded batching; shuffled reproducibly from the seed (§6.5).
            var items = trainItems.Select(item => random.NextDouble() < anyShare ? item with { StyleId = 0 } : item).ToList();
            var batches = Batcher.Bucket(items, batchSize: 32, random);
            double total = 0;
            long beats = 0;
            int done = 0;
            foreach (var batch in batches)
            {
                long batchBeats = batch.Sum(static x => (long)x.Length);
                total += trainer.TrainBatch(batch) * batchBeats;
                beats += batchBeats;
                done += batch.Count;
                if (done % 2000 < batch.Count)
                    Console.WriteLine($"  epoch {epoch}: {done}/{items.Count} parts, NLL/beat {total / Math.Max(1, beats):F4}");
            }

            double heldOutLoss = HeldOutLoss(trainer, heldOutItems);
            Console.WriteLine($"Epoch {epoch}: train NLL/beat {total / Math.Max(1, beats):F4}, held-out NLL/beat {heldOutLoss:F4}.");
            if (heldOutLoss < best - 1e-5)
            {
                best = heldOutLoss;
                bestWeights = trainer.Export();
                stale = 0;
            }
            else if (++stale >= patience)
            {
                Console.WriteLine($"Early stopping (held-out NLL stopped improving, §6.5). Best: {best:F4}.");
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
            [.. styles.Select((st, i) => new PackStyle(i, st, i == 0 ? "Blend of all styles (§3.3 Any)." : $"Trained style: {st}."))],
            [.. instruments],
            [.. pairs],
            new PackInputs(useRank, true, hidden, embed),
            gaps,
            new Dictionary<string, double[]> { ["Easy"] = [0.5, 2.0], ["Medium"] = [0.5, 2.0], ["Hard"] = [0.5, 2.0] },
            [.. BeatPattern.Vocabulary],
            new PackTrainingSummary(
                trainParts.Select(static x => x.SongKey).Distinct().Count(),
                trainParts.Count,
                string.Join(",", styles.Skip(1).Take(5)),
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                "DifficultyTrainer 2.0",
                $"Held-out NLL/beat {best:F4}."),
            "See pack repository license.",
            "");

        var pack = new NeuralPack { Manifest = manifest, Weights = bestWeights };
        File.WriteAllBytes(output, pack.ToBytes());
        Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024} KB, {bestWeights.ParameterCount} params).");
        Console.WriteLine($"Min gaps (48ths of a beat): {string.Join(", ", gaps.Select(static kv => kv.Key + "=" + kv.Value))}.");
        return 0;
    }

    /// <summary>The songs held out of training, as Prepare split them.</summary>
    internal static HashSet<string> HeldOutKeys(string input)
    {
        string path = input + ".heldout";
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Where(static l => l.Length > 0)]
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>A prepared part as the trainer feeds it, its style as prepared.</summary>
    internal static BatchItem ItemOf(PreparedPart part, string[] styles, string[] pairs, string[] instruments, bool useRank)
    {
        int n = part.SourcePatterns.Length;
        var features = new float[n * BeatFeatures.Count];
        for (int i = 0; i < n; i++)
            BeatFeatures.Write(part.ScalarsAt(i), part.SourceRank, useRank, features.AsSpan(i * BeatFeatures.Count, BeatFeatures.Count));
        return new BatchItem(
            part.SourcePatterns,
            part.Candidates(),
            features,
            part.Labels,
            Math.Max(0, IndexOf(styles, part.Style)),
            Math.Max(0, IndexOf(pairs, part.Pair)),
            Math.Max(0, IndexOf(instruments, part.Instrument)));
    }

    private static double HeldOutLoss(NeuralTrainer trainer, List<BatchItem> items)
    {
        if (items.Count == 0)
            return double.PositiveInfinity;
        double total = 0;
        long beats = 0;
        foreach (var batch in Batcher.Bucket(items, 32, new Random(0)))
        {
            long batchBeats = batch.Sum(static x => (long)x.Length);
            total += trainer.EvalBatch(batch) * batchBeats;
            beats += batchBeats;
        }

        return beats == 0 ? double.PositiveInfinity : total / beats;
    }

    private static int IndexOf(string[] names, string name)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Minimum gaps per target, measured from the training charts (§3.5): the 1st percentile of the gaps between the
    /// official target's kept onsets, in 48ths of a beat (the app scales by the chart's resolution), within a beat and
    /// across beats. A floor below Rules' thinning grid, so closer pairs the official charts have still fit.
    /// </summary>
    internal static Dictionary<string, long> MeasureGaps(IEnumerable<PreparedPart> parts)
    {
        var byTarget = new Dictionary<string, List<long>>
        {
            ["Easy"] = [],
            ["Medium"] = [],
            ["Hard"] = [],
        };
        foreach (var part in parts)
        {
            string target = part.Pair.Split("->").Last();
            if (!byTarget.TryGetValue(target, out var gaps))
                continue;
            long last = long.MinValue;
            for (int i = 0; i < part.Labels.Length; i++)
            {
                foreach (int slot in BeatPattern.VocabularyOffsets[part.Labels[i]])
                {
                    long at = ((long)i * BeatPattern.UnitsPerBeat) + slot;
                    if (last != long.MinValue)
                        gaps.Add(at - last);
                    last = at;
                }
            }
        }

        var result = new Dictionary<string, long>();
        foreach (var (target, gaps) in byTarget)
        {
            gaps.Sort();
            result[target] = gaps.Count == 0 ? 12 : Math.Max(6, gaps[Math.Min(gaps.Count - 1, gaps.Count / 100)]);
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
