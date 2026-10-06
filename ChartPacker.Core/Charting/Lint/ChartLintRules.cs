namespace ChartPacker.Core.Charting.Lint;

/// <summary>The chart lint rules there are (charter-spec §6), which <see cref="ChartLinter"/> runs by default.</summary>
/// <remarks>
/// Rules are listed here rather than found by reflection, which Native AOT can't do safely. None are written yet: CM2 and
/// CM3 add the CHR rules here, each with its ID in <see cref="Validation.ChrRules"/>.
/// </remarks>
public static class ChartLintRules
{
    /// <summary>Every rule, in the order they're run.</summary>
    public static IReadOnlyList<IChartLintRule> All { get; } = [];
}
