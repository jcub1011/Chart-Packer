using Serilog;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// The commands of one gesture, such as a drag or a paste, made and undone as one step (charter-spec §4.1). They're
/// applied in order, each to the chart the ones before it left, and reverted in reverse.
/// </summary>
public sealed class CompositeCommand : ChartCommand
{
    private readonly IChartCommand[] _commands;

    /// <param name="commands">The commands, in the order they're applied. There can be none.</param>
    public CompositeCommand(IEnumerable<IChartCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = [.. commands];
        if (_commands.Any(c => c is null))
            throw new ArgumentException("A composite command holds commands.", nameof(commands));
    }

    private static ILogger Logger => Log.ForContext<CompositeCommand>();

    /// <summary>The commands, in the order they're applied.</summary>
    public IReadOnlyList<IChartCommand> Commands => _commands;

    /// <summary>
    /// Applies each command in turn, all or nothing: when one throws, those applied already are reverted, in reverse,
    /// and the exception is thrown on.
    /// </summary>
    private protected override ChartChange Do(EditableChart chart)
    {
        int applied = 0;
        try
        {
            for (; applied < _commands.Length; applied++)
                _commands[applied].Apply(chart);
        }
        catch (Exception e)
        {
            Logger.Debug(e, "Command {Index} of {Count}, {Command}, failed, so the {Applied} before it were reverted", applied + 1, _commands.Length, _commands[applied].GetType().Name, applied);
            for (int i = applied - 1; i >= 0; i--)
                _commands[i].Revert(chart);
            throw;
        }

        return ChartChange.Union(_commands.Select(c => c.Affected));
    }

    private protected override void Undo(EditableChart chart)
    {
        for (int i = _commands.Length - 1; i >= 0; i--)
            _commands[i].Revert(chart);
    }
}
