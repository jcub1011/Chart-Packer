using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// A run of phrases a generator proposed for one part, drawn as ghosts, each kept or dropped by the author, until the kept
/// ones are added as one undoable step or the run is cancelled: Generate fills (<see cref="FillProposals"/>). Phrases are
/// never added without the author's say.
/// </summary>
/// <remarks>
/// The proposals were worked out from the chart as it was, so any change to the chart but the run's own, or to the
/// song's settings, ends the run, and the status says so. A gesture's preview doesn't, as it's taken back or committed.
/// </remarks>
public abstract class PhraseProposals
{
    private readonly List<Phrase> _phrases = [];
    private readonly List<bool> _kept = [];
    private bool _accepting;

    private protected PhraseProposals(EditorSession session) => Session = session;

    /// <summary>Whether a run is going on: there are proposals to keep or drop.</summary>
    public bool IsActive => _phrases.Count > 0;

    /// <summary>The part the phrases are proposed for; null while no run is going on.</summary>
    public abstract ChartInstrument? ProposedPart { get; }

    /// <summary>The kind of phrase proposed, whose row of the phrase rail the ghosts are drawn in.</summary>
    public abstract PhraseKind Kind { get; }

    /// <summary>The phrases proposed, earliest first.</summary>
    public IReadOnlyList<Phrase> Phrases => _phrases;

    /// <summary>How many of the proposals are kept.</summary>
    public int KeptCount => _kept.Count(static k => k);

    /// <summary>Whether the proposal at <paramref name="index"/> is kept, as each is until it's dropped.</summary>
    public bool IsKept(int index) => _kept[index];

    private protected EditorSession Session { get; }

    /// <summary>What the status says when Add is pressed with none kept.</summary>
    private protected abstract string NoneKeptStatus { get; }

    /// <summary>What the status says when a change to the chart ends the run.</summary>
    private protected abstract string DroppedStatus { get; }

    /// <summary>The index of the first proposal starting after <paramref name="tick"/>; null for none.</summary>
    public int? NextAfter(long tick)
    {
        for (int i = 0; i < _phrases.Count; i++)
        {
            if (_phrases[i].Tick > tick)
                return i;
        }

        return null;
    }

    /// <summary>The index of the last proposal starting before <paramref name="tick"/>; null for none.</summary>
    public int? PreviousBefore(long tick)
    {
        for (int i = _phrases.Count - 1; i >= 0; i--)
        {
            if (_phrases[i].Tick < tick)
                return i;
        }

        return null;
    }

    /// <summary>Moves the cursor to the start of the first proposal after it, which scrolls the lanes there.</summary>
    /// <returns>Whether there was one.</returns>
    public bool GoToNext() => GoTo(NextAfter(CursorTick()));

    /// <summary>Moves the cursor to the start of the last proposal before it, which scrolls the lanes there.</summary>
    /// <returns>Whether there was one.</returns>
    public bool GoToPrevious() => GoTo(PreviousBefore(CursorTick()));

    /// <summary>Keeps the proposal at <paramref name="index"/> if it's dropped, or drops it if it's kept.</summary>
    public void Toggle(int index)
    {
        _kept[index] = !_kept[index];
        Session.RaiseState();
    }

    /// <summary>Keeps every proposal.</summary>
    public void KeepAll() => SetAll(true);

    /// <summary>Drops every proposal.</summary>
    public void DropAll() => SetAll(false);

    /// <summary>
    /// Adds the kept phrases as one undoable step and ends the run. With none kept, or when the chart refuses one, nothing
    /// is added, the status says why, and the run goes on.
    /// </summary>
    /// <returns>Whether they were added.</returns>
    public bool Accept()
    {
        if (!IsActive || ProposedPart is not { } part)
            return false;
        var kept = _phrases.Where((_, i) => _kept[i]).Select(p => (IChartCommand)new AddPhrase(part, p)).ToList();
        if (kept.Count == 0)
        {
            Session.Status = NoneKeptStatus;
            return false;
        }

        Session.Tools.Cancel();
        _accepting = true;
        bool added;
        try
        {
            added = Session.Execute(new CompositeCommand(kept));
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
    /// The index of the proposal whose ghost is under <paramref name="point"/> in the rail's row for <see cref="Kind"/>
    /// beside a lane of <see cref="ProposedPart"/>, or null for none. A ghost is hit as a phrase's bar is
    /// (<see cref="PhraseRail.HitTest"/>): up to the point's tolerance beyond its ends, the nearest first.
    /// </summary>
    public int? HitTest(ChartPoint point)
    {
        if (!IsActive || point.Track.Instrument != ProposedPart || point.Rail?.Phrase != Kind)
            return null;
        int? best = null;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _phrases.Count; i++)
        {
            var phrase = _phrases[i];
            long end = phrase.Tick + phrase.Length;
            if (point.Tick < phrase.Tick - point.Tolerance || point.Tick > end + point.Tolerance)
                continue;
            double distance = point.Tick < phrase.Tick ? phrase.Tick - point.Tick : Math.Max(0, point.Tick - end);
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
        Session.Status = DroppedStatus;
    }

    /// <summary>Ends any gesture and every run going on, this one too, before a new one is worked out.</summary>
    private protected void Clear()
    {
        Session.Tools.Cancel();
        Session.Fills.Cancel();
    }

    /// <summary>Starts the run with <paramref name="phrases"/>, earliest first, every one kept.</summary>
    private protected void Begin(IEnumerable<Phrase> phrases)
    {
        _phrases.AddRange(phrases);
        _kept.AddRange(_phrases.Select(static _ => true));
        Session.Status = null;
        Session.RaiseState();
    }

    /// <summary>Forgets what the run proposed, as it ends.</summary>
    private protected abstract void Forget();

    private void End()
    {
        _phrases.Clear();
        _kept.Clear();
        Forget();
        Session.RaiseState();
    }

    // The cursor's tick as it is, not snapped: a coarse grid would round it off the proposal it was moved to, which would be
    // found again.
    private long CursorTick() => (long)Math.Round(Session.TickAt(Session.Viewport.Cursor));

    private bool GoTo(int? index)
    {
        if (index is not { } i)
            return false;
        Session.MoveCursorTo(_phrases[i].Tick);
        return true;
    }

    private void SetAll(bool kept)
    {
        if (!IsActive)
            return;
        for (int i = 0; i < _kept.Count; i++)
            _kept[i] = kept;
        Session.RaiseState();
    }
}
