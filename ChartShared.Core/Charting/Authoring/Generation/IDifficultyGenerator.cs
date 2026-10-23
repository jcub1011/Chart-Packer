using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Why a learned generator fell back to <see cref="GeneratorKind.Rules"/> for one part (spec 3.7).</summary>
public sealed record GenerationFallback(string Reason);

/// <summary>A swappable difficulty generator (spec 8). Each generator produces the same kind of
/// draft (<see cref="ReductionResult"/>) so Reduce, Reduce all and the Validator can run any of them.</summary>
public interface IDifficultyGenerator
{
    GeneratorKind Kind { get; }

    string DisplayName { get; }

    /// <summary>Whether this generator can reduce a part of <paramref name="kind"/>.
    /// Learned generators return false for drums (and GHL for now); callers fall back to Rules.</summary>
    bool Supports(ChartInstrumentKind kind);

    /// <summary>A draft of <paramref name="to"/> of <paramref name="part"/> from <paramref name="from"/>.
/// Voicing and attributes always follow <paramref name="voiceRules"/> (spec 3.4).</summary>
    (ReductionResult Result, GenerationFallback? Fallback) Reduce(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        FretReductionRules voiceRules,
        (long From, long To)? range = null,
        ChartReadOptions? options = null);
}
