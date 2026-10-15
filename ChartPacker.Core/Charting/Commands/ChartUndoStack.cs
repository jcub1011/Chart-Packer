using Serilog;
using Serilog.Events;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>How a command was run on the chart.</summary>
public enum ChartCommandAction
{
    /// <summary>Applied for the first time, by <see cref="ChartUndoStack.Execute"/>.</summary>
    Do,

    /// <summary>Reverted, by <see cref="ChartUndoStack.Undo"/>.</summary>
    Undo,

    /// <summary>Applied again, by <see cref="ChartUndoStack.Redo"/>.</summary>
    Redo,
}

/// <summary>A command the undo stack has just run, and how.</summary>
public sealed class ChartCommandEventArgs(IChartCommand command, ChartCommandAction action) : EventArgs
{
    public IChartCommand Command { get; } = command;

    public ChartCommandAction Action { get; } = action;

    /// <summary>What the command touched, whichever way it ran.</summary>
    public ChartChange Affected => Command.Affected;
}

/// <summary>The state of a <see cref="ChartUndoStack"/> a save was taken at (<see cref="ChartUndoStack.Mark"/>).</summary>
/// <param name="Count">How many commands were done.</param>
/// <param name="Top">The last of them, or null when none were.</param>
public readonly record struct SavePoint(int Count, IChartCommand? Top);

/// <summary>
/// The chart's undo and redo (charter-spec C15 item 1): every change goes through <see cref="Execute"/>, and is undone
/// and redone in order. It keeps every step of the session, as commands store changes rather than copies of the chart
/// (§4.1, §11).
/// </summary>
/// <remarks>
/// It belongs to the editing thread, as the chart does. Work off that thread reads a <see cref="EditableChart.Clone"/>
/// taken when it starts, which no command touches.
/// </remarks>
public sealed class ChartUndoStack
{
    private readonly List<IChartCommand> _done = [];
    private readonly List<IChartCommand> _undone = [];

    // How many commands were done when the chart was last saved, or -1 when that state can't be reached again.
    private int _saved;

    /// <param name="chart">The chart, as it was opened or created.</param>
    /// <param name="startsSaved">
    /// Whether the chart as given counts as saved: false for a chart recovered from an autosave, which differs from the
    /// one on disk until it's saved.
    /// </param>
    public ChartUndoStack(EditableChart chart, bool startsSaved = true)
    {
        ArgumentNullException.ThrowIfNull(chart);
        Chart = chart;
        _saved = startsSaved ? 0 : -1;
    }

    private static ILogger Logger => Log.ForContext<ChartUndoStack>();

    /// <summary>Raised after each command is done, undone or redone, for the views and chart lint to follow.</summary>
    public event EventHandler<ChartCommandEventArgs>? Changed;

    /// <summary>The chart the commands change.</summary>
    public EditableChart Chart { get; }

    public bool CanUndo => _done.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    /// <summary>How many commands can be undone.</summary>
    public int UndoCount => _done.Count;

    /// <summary>How many commands can be redone.</summary>
    public int RedoCount => _undone.Count;

    /// <summary>The command <see cref="Undo"/> would revert, or null when there's none.</summary>
    public IChartCommand? NextUndo => _done.Count > 0 ? _done[^1] : null;

    /// <summary>The command <see cref="Redo"/> would apply, or null when there's none.</summary>
    public IChartCommand? NextRedo => _undone.Count > 0 ? _undone[^1] : null;

    /// <summary>
    /// Whether the chart differs from when <see cref="MarkSaved"/> was last called (or the stack was made): true after any
    /// change, false again when undoing or redoing comes back to it, and true for good once a new command drops the
    /// saved state from the redo steps.
    /// </summary>
    public bool IsDirty => _done.Count != _saved;

    /// <summary>Applies <paramref name="command"/> and keeps it to undo. The redo steps are dropped.</summary>
    /// <exception cref="InvalidOperationException">
    /// The command can't be applied (see <see cref="IChartCommand.Apply"/>); the chart and the stack are as they were.
    /// </exception>
    public void Execute(IChartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(Chart);
        if (_saved > _done.Count)
            _saved = -1;
        _undone.Clear();
        _done.Add(command);
        Raise(command, ChartCommandAction.Do);
    }

    /// <summary>Reverts the last command done.</summary>
    /// <exception cref="InvalidOperationException">There's nothing to undo.</exception>
    public void Undo()
    {
        if (_done.Count == 0)
            throw new InvalidOperationException("There's nothing to undo.");
        var command = _done[^1];
        command.Revert(Chart);
        _done.RemoveAt(_done.Count - 1);
        _undone.Add(command);
        Raise(command, ChartCommandAction.Undo);
    }

    /// <summary>Applies again the last command undone.</summary>
    /// <exception cref="InvalidOperationException">There's nothing to redo.</exception>
    public void Redo()
    {
        if (_undone.Count == 0)
            throw new InvalidOperationException("There's nothing to redo.");
        var command = _undone[^1];
        command.Apply(Chart);
        _undone.RemoveAt(_undone.Count - 1);
        _done.Add(command);
        Raise(command, ChartCommandAction.Redo);
    }

    /// <summary>Marks the chart as it is now as saved, so it isn't <see cref="IsDirty"/>.</summary>
    public void MarkSaved() => _saved = _done.Count;

    /// <summary>
    /// Where the chart is now, for a save that takes its snapshot here and finishes later: <see cref="MarkSaved(SavePoint)"/>
    /// then marks this state as saved, whatever was done in the meantime.
    /// </summary>
    public SavePoint Mark() => new(_done.Count, NextUndo);

    /// <summary>
    /// Marks the chart as it was at <paramref name="point"/> as saved. The chart is then <see cref="IsDirty"/> if it's
    /// been changed since, and comes back to saved when undoing or redoing reaches that point again. If a new command
    /// has since dropped that point from the redo steps, it can't be reached, and the chart stays dirty.
    /// </summary>
    public void MarkSaved(SavePoint point) => _saved = CommandAt(point.Count - 1) == point.Top ? point.Count : -1;

    // The command that brings the chart to index + 1 commands done: from those done, then those undone in redo order.
    private IChartCommand? CommandAt(int index)
    {
        if (index < 0)
            return null;
        if (index < _done.Count)
            return _done[index];
        int redo = _undone.Count - 1 - (index - _done.Count);
        return redo >= 0 ? _undone[redo] : null;
    }

    private void Raise(IChartCommand command, ChartCommandAction action)
    {
        var logger = Logger;
        if (logger.IsEnabled(LogEventLevel.Debug))
            logger.Debug("{Action} {Command}, touching {Affected}; {Undo} to undo, {Redo} to redo", action, command.GetType().Name, command.Affected.ToString(), _done.Count, _undone.Count);
        Changed?.Invoke(this, new ChartCommandEventArgs(command, action));
    }
}
