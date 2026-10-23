namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Difficulty generator kinds. <see cref="GeneratorKind.Rules"/> is today's Reduce;
/// <see cref="GeneratorKind.Patterns"/> is the count-based beat-pattern model (spec 3.2);
/// <see cref="GeneratorKind.Neural"/> is the future recurrent model (spec 3.3).</summary>
public enum GeneratorKind
{
    Rules,
    Patterns,
    Neural,
}
