using ChartShared.Core;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;

namespace DifficultyTrainer;

/// <summary>
/// Draft: runs an installed neural pack over a chart file and reports the draft's figures.
/// Read-only verification for pack authors (the apps' Reduce dialog is the charter-facing UI).
/// </summary>
internal static class Draft
{
    public static int Run(string[] args)
    {
        string mid = Arg(args, "--mid", "");
        string model = Arg(args, "--model", "official-studio-v1");
        string partName = Arg(args, "--part", "Guitar");
        string fromName = Arg(args, "--from", "Expert");
        string toName = Arg(args, "--to", "Easy");
        string style = Arg(args, "--style", "Any");
        double density = double.TryParse(Arg(args, "--density", "0"), out double d) ? d : 0;
        if (!File.Exists(mid))
        {
            Console.Error.WriteLine("Usage: draft --mid <notes.mid> [--model <name>] [--part Guitar] [--from Expert] [--to Easy] [--style Any] [--density 0]");
            return 2;
        }

        if (!Enum.TryParse(partName, true, out ChartInstrument part)
            || !Enum.TryParse(fromName, true, out ChartDifficulty from)
            || !Enum.TryParse(toName, true, out ChartDifficulty to))
        {
            Console.Error.WriteLine("Unknown part or difficulty.");
            return 2;
        }

        var store = new GeneratorModelStore(
            new GeneratorModelFolderFile(UserConfigPaths.GeneratorModelSettingsFile),
            UserConfigPaths.GeneratorModelsFolder);
        var kind = ChartInstruments.KindOf(part);
        var generator = new NeuralDifficultyGenerator(store, t => FretReductionRules.Default(kind, t));

        EditableChart chart;
        try
        {
            chart = MidiChartLoader.Load(File.ReadAllBytes(mid), ChartReadOptions.Default).Chart;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load chart: {ex.Message}");
            return 1;
        }

        var options = new GenerationOptions(Style: style, DensityBias: density, ModelName: model);
        var result = generator.Draft(chart, part, from, to, options, range: null, ChartReadOptions.Default, out string? reason);
        if (result is null)
        {
            Console.WriteLine($"Fell back to Rules: {reason}");
            return 0;
        }

        var source = chart.PartOf(part)![from];
        var sourceTicks = source.Notes.AsSpan().ToArray().Select(static n => n.Tick).Order().ToArray();
        var kept = result.Notes.Select(static n => n.Tick).Distinct().Order().ToList();
        var sourceSet = sourceTicks.ToHashSet();
        long onBeat = kept.Count(t => sourceSet.Contains(t));
        int beatLen = Math.Max(1, chart.Resolution * 4 / Math.Max(1, chart.TempoMap.ToTempoMap().TimeSignatures[0].Denominator));
        int offBeat = kept.Count(t => t % beatLen != 0);
        int beats = BeatPipeline.Extract(chart, part, from).Count;
        int empty = beats - kept.Select(t => BeatOf(t, chart)).Distinct().Count();

        Console.WriteLine($"Draft {from}->{to} ({part}, style {style}): {kept.Count} kept of {sourceTicks.Length} source onsets.");
        var laneNames = new[] { "green", "red", "yellow", "blue", "orange" };
        int topLane = result.Notes.Count > 0 ? result.Notes.Max(static n => n.Lane) : -2;
        Console.WriteLine($"  Highest lane: {(topLane < 0 ? "open" : laneNames[Math.Min(topLane, laneNames.Length - 1)] + $" ({topLane})")}.");
        Console.WriteLine($"  On an Expert onset: {100.0 * onBeat / Math.Max(1, kept.Count):F1}% (official charts: ~99%, §5.1).");
        Console.WriteLine($"  Off-beat share: {100.0 * offBeat / Math.Max(1, kept.Count):F1}% of kept chords.");
        Console.WriteLine($"  Rests: {empty} of {beats} source beats left empty.");
        return 0;

        static long BeatOf(long tick, EditableChart c)
        {
            var map = c.TempoMap.ToTempoMap();
            var sig = map.TimeSignatures[0];
            foreach (var s in map.TimeSignatures)
            {
                if (s.Tick > tick)
                    break;
                sig = s;
            }

            long beat = Math.Max(1, c.Resolution * 4L / Math.Max(1, sig.Denominator));
            return (tick - sig.Tick) / beat;
        }
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
