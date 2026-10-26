using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// The Neural generator (spec §3.3): BiGRU + CRF rhythm choice over beat patterns, voiced by
/// Rules' logic (§3.4 steps 5–6; learned voicing is M7). Falls back to Rules with a reason
/// whenever it can't run (§3.7).
/// </summary>
public sealed class NeuralDifficultyGenerator : IDifficultyGenerator
{
    public const string NeuralMethodId = "neural";

    private readonly IGeneratorModelStore _store;
    private readonly Func<ChartInstrument, ChartDifficulty, FretReductionRules> _voiceRules;
    private readonly NeuralPackCache _cache;

    public string MethodId => NeuralMethodId;

    /// <param name="voiceRules">Rules' voicing and attributes per part and target (§3.4 steps 5–6), and the rules the
    /// caller falls back to.</param>
    /// <param name="cache">Where packs are loaded; the apps' shared cache when null.</param>
    public NeuralDifficultyGenerator(IGeneratorModelStore store, Func<ChartInstrument, ChartDifficulty, FretReductionRules> voiceRules, NeuralPackCache? cache = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _voiceRules = voiceRules ?? throw new ArgumentNullException(nameof(voiceRules));
        _cache = cache ?? NeuralPackCache.Shared;
    }

    /// <summary>The fret families a pack can cover (§1.2, §3.7); drums keep Rules. Each pack names the parts it covers.</summary>
    public bool CanRunFor(ChartInstrumentKind kind) =>
        kind is ChartInstrumentKind.FiveFret or ChartInstrumentKind.SixFret;

    /// <summary>Installed neural packs, by model name.</summary>
    public IReadOnlyList<string> InstalledPacks() => InstalledPacks(_store);

    /// <summary>The neural packs installed in <paramref name="store"/>, by model name; none when it can't be listed.</summary>
    public static IReadOnlyList<string> InstalledPacks(IGeneratorModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return [.. store.List()
                .Where(static m => m.Id.Type == GeneratorType.NeuralDifficulty)
                .Select(static m => m.Id.Name)
                .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public ReductionResult? Draft(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        GenerationOptions options,
        (long From, long To)? range,
        ChartReadOptions? readOptions,
        out string? note)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        note = null;
        var kind = ChartInstruments.KindOf(part);
        if (!CanRunFor(kind))
        {
            note = $"Neural has no pack for {kind}; its draft is Rules'.";
            return null;
        }

        string? modelName = options.ModelName ?? InstalledPacks().FirstOrDefault();
        if (modelName is null)
        {
            note = "No neural pack is installed; its draft is Rules'. Get packs to enable it.";
            return null;
        }

        NeuralPack pack;
        BiGruCrf model;
        try
        {
            (pack, model) = _cache.Load(_store, modelName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            note = $"Neural pack '{modelName}' failed to load ({ex.Message}); its draft is Rules'.";
            return null;
        }

        var manifest = pack.Manifest;
        if (NeuralConditioning.InstrumentIndex(manifest.Instruments, part) < 0)
        {
            note = $"Neural pack '{modelName}' wasn't trained on {part}; its draft is Rules'.";
            return null;
        }

        string pair = NeuralConditioning.PairName(from, to);
        if (NeuralConditioning.PairIndex(manifest.Pairs, from, to) < 0)
        {
            note = $"Neural pack '{modelName}' wasn't trained on {pair}; its draft is Rules'.";
            return null;
        }

        // A pair the pack lacks drafts as the nearest it has, and says so (§3.6).
        if (!manifest.Pairs.Contains(pair, StringComparer.OrdinalIgnoreCase))
            note = $"Neural pack '{modelName}' wasn't trained on {pair}; it drafted {to} as it does from Expert.";

        try
        {
            return ReduceWithModel(chart, part, from, to, pack, model, options, range, readOptions);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            note = $"Neural failed ({ex.Message}); its draft is Rules'.";
            return null;
        }
    }

    private ReductionResult ReduceWithModel(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        NeuralPack pack,
        BiGruCrf model,
        GenerationOptions options,
        (long From, long To)? range,
        ChartReadOptions? readOptions)
    {
        var kind = ChartInstruments.KindOf(part);
        var rules = _voiceRules(part, to);
        if (!options.CapLanes)
            rules = rules with { HighestLane = kind == ChartInstrumentKind.SixFret ? 2 : 4 };

        // The whole part is read for context; only the range is decoded and voiced (§3.6).
        var beats = BeatPipeline.Extract(chart, part, from);
        if (beats.Count == 0)
            return Reducer.Voice(chart, part, from, to, rules, new HashSet<long>(), range, readOptions);

        double? rank = pack.Manifest.Inputs.UseRank ? NeuralConditioning.SourceRank(chart, part, from, readOptions) : null;
        var conditioning = new RunConditioning(from, to, part, StyleIdOf(pack, options.Style), rank);
        var candidateSets = beats.Select(static b => new HashSet<int>(BeatPattern.CandidatesFor(b.SourceOffsets48))).ToList();
        var emissions = model.Emissions(beats, conditioning, candidateSets);

        // The floor, in 48ths of a beat: the run's, or the pack's for the target (§3.5).
        long floor48 = options.MinGapTicks > 0
            ? options.MinGapTicks
            : pack.Manifest.MinGapTicks.TryGetValue(to.ToString(), out long gap48) ? gap48 : 0;
        IReadOnlyCollection<long>? fixedTicks = range is { } r
            ? [.. chart.PartOf(part)![to].Notes.AsSpan().ToArray().Where(n => n.Tick < r.From || n.Tick > r.To).Select(static n => n.Tick).Distinct()]
            : null;

        var constraints = new ViterbiDecoder.DecodeConstraints(
            floor48 * chart.Resolution / BeatPattern.UnitsPerBeat, RequireStarPowerSolo: true, options.DensityBias,
            candidateSets, beats, range, fixedTicks);
        int[] choice = ViterbiDecoder.Decode(
            emissions, pack.Weights.Transition, pack.Weights.StartTransition, pack.Weights.EndTransition, constraints);

        var keep = new HashSet<long>();
        for (int i = 0; i < beats.Count; i++)
            keep.UnionWith(ViterbiDecoder.KeptTicks(beats[i], choice[i]));
        return Reducer.Voice(chart, part, from, to, rules, keep, range, readOptions);
    }

    private static int StyleIdOf(NeuralPack pack, string style)
    {
        var match = pack.Manifest.Styles.FirstOrDefault(s =>
            string.Equals(s.Label, style, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match.Id;
        // Any (id 0) is the default (§3.3).
        return 0;
    }
}
