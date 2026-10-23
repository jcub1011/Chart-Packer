using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Today's Reduce as a swappable generator (spec 3.1, 8). Behaviour is unchanged;
/// it is the fallback every learned generator uses.</summary>
public sealed class RulesGenerator : IDifficultyGenerator
{
    public GeneratorKind Kind => GeneratorKind.Rules;

    public string DisplayName => "Rules";

    public bool Supports(ChartInstrumentKind kind) => true;

    public (ReductionResult Result, GenerationFallback? Fallback) Reduce(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        FretReductionRules voiceRules,
        (long From, long To)? range = null,
        ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(voiceRules);
        var kind = ChartInstruments.KindOf(part);
        if (kind == ChartInstrumentKind.Drums)
            throw new ArgumentException("Drums are reduced by the drum rules.", nameof(part));
        var result = Reducer.Reduce(chart, part, from, to, voiceRules, range, options);
        return (result, null);
    }
}
