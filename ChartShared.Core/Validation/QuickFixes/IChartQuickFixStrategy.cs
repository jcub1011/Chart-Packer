using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Validation.QuickFixes;

/// <summary>
/// Works out the ways to fix chart lint findings of one rule (charter-spec C16 item 3): each a command that changes the
/// chart, which the editor runs through its undo stack, so a fix is one step to undo. Where there's more than one right
/// answer, such as which of two notes too close together to delete, each is a <see cref="ChartFixOption"/> of its own,
/// for the author to pick. It's registered in <see cref="QuickFixRegistry.ChartStrategies"/>.
/// </summary>
/// <remarks>
/// It changes only the chart. Fixes that change song.ini instead, such as CHR084's, go through Chart Builder's pending
/// song.ini changes (<see cref="Charting.Lint.PendingSongIniFixes"/>).
/// </remarks>
public interface IChartQuickFixStrategy
{
    /// <summary>The rule whose findings this fixes, from <see cref="ChrRules"/>.</summary>
    string RuleId { get; }

    /// <summary>The ways to fix <paramref name="finding"/>, worked out from <paramref name="chart"/> as it is now.</summary>
    /// <param name="finding">A finding of <see cref="RuleId"/>, with its chart location.</param>
    /// <param name="chart">The chart the finding was found in, as it is now; the strategy doesn't change it.</param>
    /// <param name="options">The settings the chart is read with, as lint read it, such as the sustain cutoff.</param>
    /// <returns>Each way, in the order to offer them; none when nothing here can fix it.</returns>
    IReadOnlyList<ChartFixOption> Propose(Finding finding, EditableChart chart, ChartReadOptions options);
}
