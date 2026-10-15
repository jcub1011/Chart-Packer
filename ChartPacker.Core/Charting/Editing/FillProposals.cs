using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A run of Generate fills (charter-spec C6 item 5): the fills <see cref="FillGenerator"/> proposed for the drums, drawn
/// as ghosts, each kept or dropped by the author, until the kept ones are added as one undoable step or the run is
/// cancelled. Fills are never added without the author's say. The session owns it (<see cref="EditorSession.Fills"/>).
/// </summary>
/// <remarks>
/// The proposals were worked out from the chart as it was, so any change to the chart but the run's own, or to the
/// song's settings, ends the run, and the status says so. A gesture's preview doesn't, as it's taken back or committed.
/// </remarks>
public sealed class FillProposals
{
    private const string NoDrumNotes = "The drums have no notes to propose fills over.";
    private const string TooFewStarPower = "Fills are proposed only after the second star power phrase ends, and the drums have fewer than two.";
    private const string NoCrashSpot = "No fill fits: none ends on a crash on a measure's first beat that every difficulty with notes has a note on, after the second star power phrase and clear of star power, other fills and [coda].";
    private const string NoNoteSpot = "No fill fits: none ends on a note on a measure's first beat that every difficulty with notes has, after the second star power phrase and clear of star power, other fills and [coda].";
    private const string NoneKept = "No proposed fill is kept, so there's nothing to add.";
    private const string Dropped = "The proposed fills were dropped, as the chart changed. Generate them again to see them.";

    private readonly EditorSession _session;
    private readonly List<FillProposal> _proposals = [];
    private readonly List<bool> _kept = [];
    private bool _accepting;

    internal FillProposals(EditorSession session) => _session = session;

    /// <summary>Whether a run is going on: there are proposals to keep or drop.</summary>
    public bool IsActive => _proposals.Count > 0;

    /// <summary>The part the fills are proposed for.</summary>
    public static ChartInstrument Part => ChartInstrument.Drums;

    /// <summary>The fills proposed, earliest first.</summary>
    public IReadOnlyList<FillProposal> Proposals => _proposals;

    /// <summary>How many of the proposals are kept.</summary>
    public int KeptCount => _kept.Count(static k => k);

    /// <summary>Whether the proposal at <paramref name="index"/> is kept, as each is until it's dropped.</summary>
    public bool IsKept(int index) => _kept[index];

    /// <summary>
    /// Starts a run, in place of any going on: proposes fills over <paramref name="span"/>, or the whole part, every one
    /// kept. When none fits, the status says why.
    /// </summary>
    /// <returns>Whether any were proposed.</returns>
    public bool Start(FillOptions options, (long From, long To)? span)
    {
        ArgumentNullException.ThrowIfNull(options);
        _session.Tools.Cancel();
        Cancel();
        var generated = FillGenerator.Propose(_session.Chart, _session.DrumsVariantShown, options, span);
        if (generated.Proposals.Count == 0)
        {
            _session.Status = generated.Reason switch
            {
                NoFillsReason.NoDrumNotes => NoDrumNotes,
                NoFillsReason.TooFewStarPower => TooFewStarPower,
                _ => options.EndOn == FillEnd.Crash ? NoCrashSpot : NoNoteSpot,
            };
            return false;
        }

        _proposals.AddRange(generated.Proposals);
        _kept.AddRange(generated.Proposals.Select(static _ => true));
        _session.Status = null;
        _session.RaiseState();
        return true;
    }

    /// <summary>Keeps the proposal at <paramref name="index"/> if it's dropped, or drops it if it's kept.</summary>
    public void Toggle(int index)
    {
        _kept[index] = !_kept[index];
        _session.RaiseState();
    }

    /// <summary>Keeps every proposal.</summary>
    public void KeepAll() => SetAll(true);

    /// <summary>Drops every proposal.</summary>
    public void DropAll() => SetAll(false);

    /// <summary>
    /// Adds the kept fills as one undoable step and ends the run. With none kept, or when the chart refuses one, nothing
    /// is added, the status says why, and the run goes on.
    /// </summary>
    /// <returns>Whether they were added.</returns>
    public bool Accept()
    {
        if (!IsActive)
            return false;
        var kept = _proposals.Where((_, i) => _kept[i]).Select(static p => (IChartCommand)new AddPhrase(Part, p.Fill)).ToList();
        if (kept.Count == 0)
        {
            _session.Status = NoneKept;
            return false;
        }

        _session.Tools.Cancel();
        _accepting = true;
        bool added;
        try
        {
            added = _session.Execute(new CompositeCommand(kept));
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

    /// <summary>
    /// The index of the proposal whose ghost is under <paramref name="point"/> in the rail's fills row beside a drums lane,
    /// or null for none. A ghost is hit as a phrase's bar is (<see cref="PhraseRail.HitTest"/>): up to the point's tolerance
    /// beyond its ends, the nearest first.
    /// </summary>
    public int? HitTest(ChartPoint point)
    {
        if (!IsActive || point.Track.Instrument != Part || point.Rail?.Phrase != PhraseKind.DrumFill)
            return null;
        int? best = null;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _proposals.Count; i++)
        {
            var fill = _proposals[i].Fill;
            long end = fill.Tick + fill.Length;
            if (point.Tick < fill.Tick - point.Tolerance || point.Tick > end + point.Tolerance)
                continue;
            double distance = point.Tick < fill.Tick ? fill.Tick - point.Tick : Math.Max(0, point.Tick - end);
            if (distance < bestDistance)
                (best, bestDistance) = (i, distance);
        }

        return best;
    }

    /// <summary>Ends the run, as the chart or the song's settings changed under it, unless the change is its own.</summary>
    internal void OnChanged()
    {
        if (!IsActive || _accepting)
            return;
        End();
        _session.Status = Dropped;
    }

    private void End()
    {
        _proposals.Clear();
        _kept.Clear();
        _session.RaiseState();
    }

    private void SetAll(bool kept)
    {
        if (!IsActive)
            return;
        for (int i = 0; i < _kept.Count; i++)
            _kept[i] = kept;
        _session.RaiseState();
    }
}
