using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// The commands of this assembly: each checks everything it needs before it changes anything, and knows whether it's
/// applied, and to which chart.
/// </summary>
public abstract class ChartCommand : IChartCommand
{
    private EditableChart? _appliedTo;

    private protected ChartCommand()
    {
    }

    /// <inheritdoc/>
    public ChartChange Affected { get; private set; } = ChartChange.None;

    /// <inheritdoc/>
    public void Apply(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (_appliedTo is not null)
            throw new InvalidOperationException("The command is already applied; revert it first.");
        Affected = Do(chart);
        _appliedTo = chart;
    }

    /// <inheritdoc/>
    public void Revert(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (!ReferenceEquals(_appliedTo, chart))
            throw new InvalidOperationException("The command isn't applied to this chart.");
        Undo(chart);
        _appliedTo = null;
    }

    /// <summary>
    /// Checks the change can be made to <paramref name="chart"/>, throwing before changing anything when it can't, then
    /// makes it, keeping what it needs to undo it.
    /// </summary>
    /// <returns>What it touched.</returns>
    private protected abstract ChartChange Do(EditableChart chart);

    /// <summary>Undoes what the last <see cref="Do"/> did to <paramref name="chart"/>.</summary>
    private protected abstract void Undo(EditableChart chart);

    /// <summary>The chart's part for <paramref name="instrument"/>.</summary>
    /// <exception cref="InvalidOperationException">The chart hasn't the part.</exception>
    private protected static EditablePart PartOf(EditableChart chart, ChartInstrument instrument) =>
        chart.PartOf(instrument) ?? throw new InvalidOperationException($"The chart has no {instrument} part.");

    /// <summary>The chart's track for <paramref name="instrument"/> at <paramref name="difficulty"/>.</summary>
    /// <exception cref="InvalidOperationException">The chart hasn't the part.</exception>
    private protected static EditableTrack TrackOf(EditableChart chart, ChartInstrument instrument, ChartDifficulty difficulty) =>
        PartOf(chart, instrument)[difficulty];

    /// <summary><paramref name="items"/> as an array, which must have at least one item.</summary>
    /// <exception cref="ArgumentException">There's none.</exception>
    private protected static T[] AtLeastOne<T>(IEnumerable<T> items, string name)
    {
        ArgumentNullException.ThrowIfNull(items, name);
        T[] array = [.. items];
        if (array.Length == 0)
            throw new ArgumentException("There's nothing to change.", name);
        return array;
    }

    /// <summary>A note, in words, for messages: "the lane 2 note at tick 480".</summary>
    private protected static string Describe(EditableNote note)
    {
        string what = note.DoubleKick ? "2x kick" : note.Lane == ChartNote.OpenLane ? "open note" : Invariant($"lane {note.Lane} note");
        return Invariant($"the {what} at tick {note.Tick}");
    }

    private protected static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
