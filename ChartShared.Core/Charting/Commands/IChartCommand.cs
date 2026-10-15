namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// One change to an <see cref="EditableChart"/> that can be undone (charter-spec §4.1, C15 item 1): adding notes, moving
/// them, editing the tempo map and so on. <see cref="ChartUndoStack"/> runs them.
/// </summary>
/// <remarks>
/// A command stores what it changed (what it added, removed or replaced, and what was there before), never a copy of
/// the chart. <see cref="Revert"/> puts the chart back exactly as <see cref="Apply"/> found it, provided every command
/// applied since has been reverted, in reverse order, as the undo stack does. Applying again after reverting (redo)
/// gives the same chart as the first apply did.
/// </remarks>
public interface IChartCommand
{
    /// <summary>
    /// What the last <see cref="Apply"/> touched; <see cref="ChartChange.None"/> until it's applied. Reverting touches
    /// the same.
    /// </summary>
    ChartChange Affected { get; }

    /// <summary>
    /// Makes the change, all or nothing: when it can't be made, it throws before changing anything.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The change can't be made to the chart as it is, such as a note onto a place that has one; or the command is
    /// already applied.
    /// </exception>
    /// <exception cref="ArgumentException">What the command was given can't be in the chart, such as a lane the part hasn't.</exception>
    void Apply(EditableChart chart);

    /// <summary>Undoes the change, putting the chart back as <see cref="Apply"/> found it.</summary>
    /// <exception cref="InvalidOperationException">The command isn't applied to <paramref name="chart"/>.</exception>
    void Revert(EditableChart chart);
}
