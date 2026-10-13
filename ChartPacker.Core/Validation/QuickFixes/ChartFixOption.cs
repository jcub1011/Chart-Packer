using ChartPacker.Core.Charting.Commands;

namespace ChartPacker.Core.Validation.QuickFixes;

/// <summary>One way to fix a chart lint finding: one undoable command (a <see cref="CompositeCommand"/> for several changes).</summary>
/// <param name="Id">
/// Which way it is, the same for every finding of its rule, so Fix All can apply this way to each of them; e.g.
/// <c>nearest</c> for CHR004's snap to the nearest grid line.
/// </param>
/// <param name="Label">
/// What it does, for the finding's menu to list it by; <see langword="null"/> for a rule with only one way to fix it,
/// whose menu needs no list.
/// </param>
/// <param name="Command">The change it makes.</param>
public sealed record ChartFixOption(string Id, string? Label, IChartCommand Command)
{
    /// <summary>The <see cref="Id"/> of a rule's only way to fix it.</summary>
    public const string Only = "fix";
}
