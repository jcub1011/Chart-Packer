using System.Text.Json;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>
/// Evaluate (spec §4.5, §6.6, M4): on the held-out songs, how close the pack's decoded drafts and Rules' drafts each come
/// to the official charts, beat for beat on the same beats: which pattern each beat keeps, and which onsets.
/// </summary>
internal static class Evaluate
{
    /// <summary>The grid the prepared beats are laid out on for decoding: any resolution works, the gaps scale with it.</summary>
    private const int Resolution = 480;

    public static int Run(string[] args)
    {
        string input = Arg(args, "--in", "training-set.json");
        string packPath = Arg(args, "--pack", "");
        int maxParts = int.TryParse(Arg(args, "--max-parts", "0"), out int mp) ? mp : 0;
        bool styles = Arg(args, "--styles", "off") == "on";
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("Usage: evaluate --in <training-set.json> [--pack <pack.neuralpack>] [--max-parts N] [--styles on]");
            return 2;
        }

        TrainingSet set;
        using (var stream = File.OpenRead(input))
            set = JsonSerializer.Deserialize(stream, TrainingSetJsonContext.Default.TrainingSet)!;
        if (set.BeatSchemaVersion != BeatPipeline.BeatSchemaVersion)
        {
            Console.Error.WriteLine($"The training set is beat schema {set.BeatSchemaVersion}; this trainer is {BeatPipeline.BeatSchemaVersion}. Run prepare again.");
            return 1;
        }

        var heldOut = Train.HeldOutKeys(input);
        var parts = set.Examples.Where(e => heldOut.Contains(e.SongKey)).ToList();
        if (parts.Count == 0)
        {
            Console.WriteLine("No held-out parts; evaluating on all parts (the figures are then on training data).");
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

        var model = pack is null ? null : new BiGruCrf(pack);
        Console.WriteLine($"Evaluating {parts.Count} held-out parts{(styles ? ", each in its prepared style" : ", style Any")}.");
        Console.WriteLine("Against the official chart, on beats the source plays: pattern = the beat's kept pattern matches; onsets = precision / recall / F1 of kept onsets; density = kept onsets ÷ official.");
        foreach (var target in new[] { "Hard", "Medium", "Easy" })
        {
            foreach (var pair in set.Pairs.Where(p => p.EndsWith("->" + target, StringComparison.Ordinal)))
            {
                var slice = parts.Where(p => p.Pair == pair).ToList();
                if (maxParts > 0)
                    slice = slice.Take(maxParts).ToList();
                if (slice.Count == 0)
                {
                    Console.WriteLine($"{pair}: no held-out parts.");
                    continue;
                }

                var rules = new Score();
                var neural = new Score();
                foreach (var part in slice)
                {
                    rules.Add(part, part.RulesLabels);
                    if (model is not null)
                        neural.Add(part, Predict(model, pack!, part, set, target, styles));
                }

                Console.WriteLine($"{pair}: {slice.Count} parts, {rules.Beats} beats.");
                Console.WriteLine($"  Rules   {rules}");
                if (model is not null)
                    Console.WriteLine($"  Neural  {neural}   ({neural.Compare(rules)})");
            }
        }

        return 0;
    }

    private static int[] Predict(BiGruCrf model, NeuralPack pack, PreparedPart part, TrainingSet set, string target, bool styles)
    {
        // Full Viterbi path decode (spec §6.6 "decoded"): riff tying, the pack's min-gap floor and star power, on the
        // part's own onsets — what the apps draft, before voicing.
        var manifest = pack.Manifest;
        var frames = part.Frames(Resolution);
        var candidateSets = part.Candidates().Select(static c => c.ToHashSet()).ToList();
        int style = styles ? Math.Max(0, manifest.Styles.FindIndex(s => string.Equals(s.Label, part.Style, StringComparison.OrdinalIgnoreCase))) : 0;
        int pair = Math.Max(0, manifest.Pairs.FindIndex(p => string.Equals(p, part.Pair, StringComparison.OrdinalIgnoreCase)));
        int instrument = Math.Max(0, manifest.Instruments.FindIndex(i => string.Equals(i, part.Instrument, StringComparison.OrdinalIgnoreCase)));
        var emissions = model.Emissions(part.SourcePatterns, part.AllScalars(), style, pair, instrument, part.SourceRank, candidateSets);
        long floor = manifest.MinGapTicks.TryGetValue(target, out long gap48) ? gap48 * Resolution / BeatPattern.UnitsPerBeat : 0;
        var constraints = new ViterbiDecoder.DecodeConstraints(floor, true, 0, candidateSets, frames);
        return ViterbiDecoder.Decode(emissions, pack.Weights.Transition, pack.Weights.StartTransition, pack.Weights.EndTransition, constraints);
    }

    // How close one generator's labels come to the official ones, summed over parts.
    private sealed class Score
    {
        public long Beats { get; private set; }
        private long _patterns;
        private long _kept;
        private long _official;
        private long _hits;

        public void Add(PreparedPart part, int[] labels)
        {
            for (int i = 0; i < part.Labels.Length && i < labels.Length; i++)
            {
                if (part.Offsets[i].Length == 0)
                    continue;
                Beats++;
                if (labels[i] == part.Labels[i])
                    _patterns++;
                var mine = BeatPattern.VocabularyOffsets[labels[i]];
                var theirs = BeatPattern.VocabularyOffsets[part.Labels[i]];
                _kept += mine.Length;
                _official += theirs.Length;
                _hits += mine.Count(theirs.Contains);
            }
        }

        private double Pattern => 100.0 * _patterns / Math.Max(1, Beats);
        private double Precision => 100.0 * _hits / Math.Max(1, _kept);
        private double Recall => 100.0 * _hits / Math.Max(1, _official);
        private double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
        private double Density => (double)_kept / Math.Max(1, _official);

        public string Compare(Score rules) =>
            $"pattern {Pattern - rules.Pattern:+0.0;-0.0} pts, F1 {F1 - rules.F1:+0.0;-0.0} pts vs Rules";

        public override string ToString() =>
            $"pattern {Pattern,5:F1}%   onsets P {Precision,5:F1}%  R {Recall,5:F1}%  F1 {F1,5:F1}%   density {Density:F2}";
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
