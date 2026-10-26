using ChartShared.Core.Charting;
namespace ChartShared.Core.Charting.Authoring;

using ChartShared.Core.Charts;

/// <summary>
/// One way of drafting a lower difficulty (spec §8: a generator seam). Rules, Patterns and Neural
/// all produce the same kind of draft (<see cref="ReductionResult"/>), so the preview, diff,
/// check and Apply work unchanged whatever the generator.
/// </summary>
public interface IDifficultyGenerator
{
    /// <summary>The method id persisted in settings (e.g. "rules", "neural").</summary>
    string MethodId { get; }

    /// <summary>Whether this generator can run for the part family at all.</summary>
    bool CanRunFor(ChartInstrumentKind kind);

    /// <summary>
    /// Drafts <paramref name="to"/> from <paramref name="from"/>. Returns null with
    /// <paramref name="fallbackReason"/> when the generator can't run for this part: the caller
    /// falls back to Rules (§3.7).
    /// </summary>
    ReductionResult? Draft(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        GenerationOptions options,
        (long From, long To)? range,
        ChartReadOptions? readOptions,
        out string? fallbackReason);
}

/// <summary>Per-run options shared by the learned generators (§3.4–§3.6).</summary>
public sealed record GenerationOptions(
    string Style = "Any",
    double DensityBias = 0,
    long MinGapTicks = 0,
    bool Chained = true,
    string? ModelName = null,
    bool CapLanes = true)
{
    /// <summary>No learned options: plain Rules behaviour.</summary>
    public static GenerationOptions Default { get; } = new();
}

/// <summary>Rules as a generator: the seam's default, and every learned generator's fallback (§3.7).</summary>
public sealed class RulesDifficultyGenerator : IDifficultyGenerator
{
    private readonly FretReductionRules? _fretRules;
    private readonly DrumReductionRules? _drumRules;
    private readonly DrumsVariant _drumsVariant;

    public string MethodId => "rules";

    public RulesDifficultyGenerator(FretReductionRules fretRules) => _fretRules = fretRules;

    public RulesDifficultyGenerator(DrumReductionRules drumRules, DrumsVariant variant = DrumsVariant.FourLane)
    {
        _drumRules = drumRules;
        _drumsVariant = variant;
    }

    public bool CanRunFor(ChartInstrumentKind kind) => true;

    public ReductionResult? Draft(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        GenerationOptions options,
        (long From, long To)? range,
        ChartReadOptions? readOptions,
        out string? fallbackReason)
    {
        fallbackReason = null;
        var kind = ChartInstruments.KindOf(part);
        if (kind == ChartInstrumentKind.Drums)
            return Reducer.Reduce(chart, from, to, _drumRules ?? DrumReductionRules.Default(to), _drumsVariant, range);
        return Reducer.Reduce(chart, part, from, to, _fretRules ?? FretReductionRules.Default(kind, to), range, readOptions);
    }
}
