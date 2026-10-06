using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;

namespace ChartPacker.Core.Validation.QuickFixes;

/// <summary>
/// Works out the fix for chart lint findings of one rule (charter-spec C16 item 3): a command that changes the chart,
/// which the editor runs through its undo stack, so the fix is one step to undo. A strategy only proposes a fix when
/// there's exactly one right answer; it's registered in <see cref="QuickFixRegistry.ChartStrategies"/>.
/// </summary>
/// <remarks>
/// It changes only the chart. Fixes that change song.ini instead, such as CHR045's "Set <c>pro_drums = True</c>" and
/// CHR083's and CHR084's, go through Chart Builder's pending song.ini changes, which CM2 adds; this doesn't cover them yet.
/// </remarks>
public interface IChartQuickFixStrategy
{
    /// <summary>The rule whose findings this fixes, from <see cref="ChrRules"/>.</summary>
    string RuleId { get; }

    /// <summary>The fix for <paramref name="finding"/>, worked out from <paramref name="chart"/> as it is now.</summary>
    /// <param name="finding">A finding of <see cref="RuleId"/>, with its chart location.</param>
    /// <param name="chart">The chart the finding was found in, as it is now; the strategy doesn't change it.</param>
    /// <returns>One undoable command (a <see cref="CompositeCommand"/> for several changes), or <see langword="null"/> when there's no single right fix.</returns>
    IChartCommand? Propose(Finding finding, EditableChart chart);
}
