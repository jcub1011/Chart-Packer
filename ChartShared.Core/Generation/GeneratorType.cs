namespace ChartShared.Core.Generation;

/// <summary>
/// What kind of generator a stored model belongs to (issue #154). The value prefixes the file
/// name, so different kinds never collide. A placeholder until the lower-difficulty generation
/// methods land; each kind keeps its own file extension (<see cref="GeneratorModelNaming"/>).
/// </summary>
public enum GeneratorType
{
    /// <summary>Placeholder for the future lower-difficulty models.</summary>
    Difficulty,

    /// <summary>BiGRU + CRF neural lower-difficulty packs (improved-difficulty-generator-spec §3.3, §5).</summary>
    NeuralDifficulty,
}
