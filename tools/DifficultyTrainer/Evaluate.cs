using System.Text.Json;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>Evaluate (spec §4.5, §6.6): decoded + teacher-forced figures per pack, target and style.</summary>
internal static class Evaluate
{
    public static int Run(string[] args)
    {
        string input = Arg(args, "--in", "training-set.json");
        string packPath = Arg(args, "--pack", "");
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("Usage: evaluate --in <training-set.json> [--pack <pack.neuralpack>]");
            return 2;
        }

        TrainingSet set;
        using (var stream = File.OpenRead(input))
            set = JsonSerializer.Deserialize(stream, TrainingSetJsonContext.Default.TrainingSet)!;

        var heldOut = new HashSet<string>(StringComparer.Ordinal);
        string heldOutPath = input + ".heldout";
        if (File.Exists(heldOutPath))
            heldOut = [.. File.ReadAllLines(heldOutPath).Where(static l => l.Length > 0)];

        var parts = set.Examples.Where(e => heldOut.Contains(e.SongKey)).ToList();
        if (parts.Count == 0)
        {
            Console.WriteLine("No held-out parts; evaluating on all parts.");
            parts = set.Examples;
        }

        NeuralPack? pack = null;
        if (!string.IsNullOrEmpty(packPath))
        {
            try
            {
                pack = NeuralPack.LoadFromBytes(File.ReadAllBytes(packPath), Path.GetFileName(packPath));
            }
            catch (PackValidationException ex)
            {
                Console.Error.WriteLine($"Pack invalid: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine($"Evaluating {parts.Count} held-out parts.");
        foreach (var target in new[] { "Easy", "Medium", "Hard" })
        {
            var slice = parts.Where(p => p.Pair.EndsWith("->" + target)).ToList();
            if (slice.Count == 0)
            {
                Console.WriteLine($"{target}: no held-out parts.");
                continue;
            }

            // Teacher-forced: labels vs nearest-count baseline + model argmax where available.
            long beats = slice.Sum(p => (long)p.Labels.Length);
            long empty = slice.Sum(p => (long)p.Labels.Count(l => l == BeatPattern.EmptyId));
            long sub = slice.Sum(p => (long)p.SourcePatterns.Zip(p.Labels, static (s, l) => s == l && l != BeatPattern.EmptyId ? 1 : 0).Sum());
            Console.WriteLine($"{target}: {slice.Count} parts, {beats} beats ({100.0 * empty / beats:F1}% empty reference, {100.0 * sub / beats:F1}% source-kept).");

            if (pack is not null)
            {
                var model = new BiGruCrf(pack);
                long agree = 0;
                long total = 0;
                foreach (var part in slice.Take(200))
                {
                    int[] pred = Predict(model, pack, part, set);
                    for (int i = 0; i < part.Labels.Length && i < pred.Length; i++)
                    {
                        if (pred[i] == part.Labels[i])
                            agree++;
                        total++;
                    }
                }

                Console.WriteLine($"  neural decoded pattern agreement: {100.0 * agree / Math.Max(1, total):F1}% (Viterbi-decoded, §6.6).");
            }
        }

        Console.WriteLine("Note: Rules baselines come from the proposals study (§5.6, §5.9): Hard 71.0%, Medium 45.7%, Easy 62.2% pattern accuracy.");
        return 0;
    }

    private static int[] Predict(BiGruCrf model, NeuralPack pack, PreparedPart part, TrainingSet set)
    {
        // Full Viterbi path decode (spec §6.6 "decoded"): the model's own earlier choices as
        // context, with riff tying and the pack's min-gap floor — not teacher-forced argmax.
        int n = part.SourcePatterns.Length;
        var conditioning = new ChartShared.Core.Generation.Neural.RunConditioning(
            part.Pair.EndsWith("Easy") ? ChartShared.Core.Charts.ChartDifficulty.Easy
            : part.Pair.EndsWith("Medium") ? ChartShared.Core.Charts.ChartDifficulty.Medium
            : ChartShared.Core.Charts.ChartDifficulty.Hard,
            ChartShared.Core.Charts.ChartDifficulty.Expert,
            ChartShared.Core.Charts.ChartInstrumentKind.FiveFret,
            Math.Max(0, Array.IndexOf(set.Styles, part.Style)),
            BeatPipeline.TierBucketOf(part.Tier));
        const int resolution = 480;
        var beats = part.SourcePatterns.Select((s, i) => new BeatFrame(
            i * resolution, 0, Math.Clamp((int)part.Scalars[i * 8], 0, 3), s,
            [0], [i * resolution], part.Scalars[i * 8 + 1],
            part.Scalars[i * 8 + 2] != 0, part.Scalars[i * 8 + 3] != 0, part.Scalars[i * 8 + 4] != 0,
            part.Scalars[i * 8 + 5] != 0, part.Scalars[i * 8 + 6] != 0, "g" + part.RiffGroups[i], part.Scalars[i * 8 + 7])).ToList();
        var candidateSets = part.Candidates.Select(static c => c.ToHashSet()).ToList();
        var emissions = model.Emissions(beats, conditioning, candidateSets);
        long floor = 0;
        string target = part.Pair.Split("->").Last();
        if (pack.Manifest.MinGapTicks.TryGetValue(target, out long gap48))
            floor = gap48 * resolution / BeatPattern.UnitsPerBeat;
        var constraints = new ViterbiDecoder.DecodeConstraints(floor, true, 0, candidateSets, beats);
        int[] packed = ViterbiDecoder.Decode(emissions, pack.Weights.Transition, pack.Weights.StartTransition, pack.Weights.EndTransition, constraints);
        // Back to BeatPattern ids (§3.4 candidates): pack vocab v1 shares the order.
        return packed;
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
