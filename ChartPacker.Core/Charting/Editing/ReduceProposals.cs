using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A run of Reduce (charter-spec C9 item 2): a draft of a lower difficulty, shown as a ghost over it until it's added as
/// one undoable step or the run is cancelled. The session owns it (<see cref="EditorSession.Reduce"/>).
/// </summary>
/// <remarks>
/// The draft is worked out from the chart as it was, so any change to the chart but the run's own ends the run, and the
/// status says so. A gesture's preview doesn't, as it's taken back or committed. Starting again with other rules, as the
/// Reduce dialog does on each change, replaces the draft.
/// </remarks>
public sealed class ReduceProposals
{
    private const string NoSourceNotes = "There are no notes to reduce.";
    private const string Dropped = "The reduced draft was dropped, as the chart changed. Reduce again to see it.";

    private readonly EditorSession _session;
    private bool _accepting;

    internal ReduceProposals(EditorSession session) => _session = session;

    /// <summary>Whether a run is going on: there's a draft to add.</summary>
    public bool IsActive => Result is not null;

    /// <summary>The difficulty reduced from.</summary>
    public ChartDifficulty? Source { get; private set; }

    /// <summary>The part and difficulty the draft is for, which it replaces.</summary>
    public ChartTrackKey? Target { get; private set; }

    /// <summary>The draft.</summary>
    public ReductionResult? Result { get; private set; }

    /// <summary>The draft as a track of its own, for the lane to draw as a ghost; it's no part of the chart.</summary>
    public EditableTrack? Ghost { get; private set; }

    /// <summary>
    /// Starts a run of a 5-fret or GHL part, in place of any going on: a draft of <paramref name="to"/> from
    /// <paramref name="from"/> by <paramref name="rules"/>.
    /// </summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool Start(ChartInstrument part, ChartDifficulty from, ChartDifficulty to, FretReductionRules rules) =>
        Start(part, from, to, () => Reducer.Reduce(_session.Chart, part, from, to, rules));

    /// <summary>Starts a run of the drums, in place of any going on.</summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool Start(ChartDifficulty from, ChartDifficulty to, DrumReductionRules rules) =>
        Start(ChartInstrument.Drums, from, to, () => Reducer.Reduce(_session.Chart, from, to, rules, _session.DrumsVariantShown));

    /// <summary>Adds the draft as one undoable step, in place of what the target had, and ends the run.</summary>
    /// <returns>Whether it was added.</returns>
    public bool Accept()
    {
        if (Result is not { } result || Target is not { } target)
            return false;
        _session.Tools.Cancel();
        bool drums = ChartInstruments.KindOf(target.Instrument) == ChartInstrumentKind.Drums;
        _accepting = true;
        bool added;
        try
        {
            added = _session.Execute(new ReplaceTrack(target.Instrument, target.Difficulty, result.Notes, result.Flags, drums ? result.Mixes : null));
        }
        finally
        {
            _accepting = false;
        }

        if (added)
            End();
        return added;
    }

    /// <summary>Ends the run, adding nothing.</summary>
    public void Cancel()
    {
        if (IsActive)
            End();
    }

    /// <summary>Ends the run, as the chart changed under it, unless the change is its own.</summary>
    internal void OnChanged()
    {
        if (!IsActive || _accepting)
            return;
        End();
        _session.Status = Dropped;
    }

    private bool Start(ChartInstrument part, ChartDifficulty from, ChartDifficulty to, Func<ReductionResult> reduce)
    {
        _session.Tools.Cancel();
        Cancel();
        if (_session.Chart.PartOf(part) is not { } source || source[from].Notes.Count == 0)
        {
            _session.Status = NoSourceNotes;
            return false;
        }

        ReductionResult result;
        try
        {
            result = reduce();
        }
        catch (Exception e) when (EditorSession.IsRefusal(e))
        {
            _session.Status = e.Message;
            return false;
        }

        var ghost = new EditableTrack(source.Kind, to);
        TrackFill.Fill(ghost, [.. result.Notes], [.. result.Flags], [.. result.Mixes]);
        Source = from;
        Target = new ChartTrackKey(part, to);
        Result = result;
        Ghost = ghost;
        _session.Status = null;
        _session.RaiseState();
        return true;
    }

    private void End()
    {
        Source = null;
        Target = null;
        Result = null;
        Ghost = null;
        _session.RaiseState();
    }
}
