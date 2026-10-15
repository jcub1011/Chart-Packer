using ChartPacker.Core.Charting.Analysis;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A proposed tempo map being looked at (charter-spec C14 item 4): beat times found in the audio, shown as a ghost over
/// the real tempo map, which the author can adjust, double, halve or shift the downbeat of, then accept as one undoable
/// step or discard.
/// </summary>
/// <remarks>
/// <para>The beats are in audio time, so the run goes on while the chart is edited, and through a change of delay; only
/// <see cref="Start"/>, <see cref="Accept"/> and <see cref="Discard"/> end or replace it.</para>
/// <para>Dragging a beat pins it there. The beats either side are spaced evenly again as far as the nearest pinned beat, or
/// two measures, whichever is nearer, which stay where they are.</para>
/// </remarks>
public sealed class TempoProposals
{
    /// <summary>A beat is never dragged nearer than this to a beat that stays where it is.</summary>
    public const double MinGapSeconds = 0.020;

    private readonly EditorSession _session;
    private BeatProposal _original = BeatProposal.None;
    private int _originalDownbeat;
    private List<double> _beats = [];
    private List<bool> _pinned = [];
    private List<(int First, int Last)> _unsure = [];

    internal TempoProposals(EditorSession session) => _session = session;

    /// <summary>Whether a proposal is being looked at.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The proposed beats, in audio seconds, ascending.</summary>
    public IReadOnlyList<double> Beats => _beats;

    /// <summary>Which beats were dragged into place.</summary>
    public IReadOnlyList<bool> Pinned => _pinned;

    /// <summary>The beat measure lines are counted from: measure 1 starts there, unless it's on the chart's start.</summary>
    public int Downbeat { get; private set; }

    /// <summary>How many beats a measure has, from the time signature where the proposal starts.</summary>
    public int BeatsPerMeasure { get; private set; } = 4;

    /// <summary>The ticks of the selection it was proposed for, or <see langword="null"/> for the whole song.</summary>
    public (long From, long To)? Span { get; private set; }

    /// <summary>The first and last beat of each stretch too unsure to trust, which the author maps by hand.</summary>
    public IReadOnlyList<(int First, int Last)> Unsure => _unsure;

    /// <summary>The tempo most of the beats are at, as proposed, or null.</summary>
    public double? Bpm { get; private set; }

    /// <summary>The beats may be at half the song's tempo.</summary>
    public bool MayBeHalfTempo { get; private set; }

    /// <summary>Looks at <paramref name="proposal"/>, in place of any proposal being looked at.</summary>
    /// <param name="downbeat">The beat measure 1 starts on.</param>
    /// <param name="span">The ticks of the selection it was proposed for, or <see langword="null"/> for the whole song.</param>
    public void Start(BeatProposal proposal, int downbeat, (long From, long To)? span = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Beats.Count < 2)
        {
            Discard();
            _session.Status = "No steady beat was found to propose a tempo map from.";
            return;
        }

        _original = proposal;
        _originalDownbeat = Math.Clamp(downbeat, 0, proposal.Beats.Count - 1);
        Span = span;
        Load(proposal, _originalDownbeat);
        IsActive = true;
        _session.RaiseState();
    }

    /// <summary>The beat within <paramref name="toleranceSeconds"/> of <paramref name="seconds"/>, nearest first; null for none.</summary>
    public int? HitTest(double seconds, double toleranceSeconds)
    {
        if (!IsActive)
            return null;
        int index = BeatMath.NearestIndex(_beats, seconds, toleranceSeconds);
        return index < 0 ? null : index;
    }

    /// <summary>
    /// Drags beat <paramref name="index"/> to <paramref name="seconds"/> and pins it there; the beats either side are spaced
    /// evenly again up to the nearest pinned beat or two measures away, which stay put.
    /// </summary>
    /// <returns>Whether anything moved.</returns>
    public bool MoveBeat(int index, double seconds)
    {
        if (!IsActive || index < 0 || index >= _beats.Count)
            return false;

        int reach = 2 * BeatsPerMeasure;
        int left = index, right = index;
        for (int i = index - 1; i >= Math.Max(0, index - reach); i--)
        {
            left = i;
            if (_pinned[i])
                break;
        }

        for (int i = index + 1; i <= Math.Min(_beats.Count - 1, index + reach); i++)
        {
            right = i;
            if (_pinned[i])
                break;
        }

        // Never so near a beat that stays put that the beats between can't keep their order.
        double low = left < index ? _beats[left] + (MinGapSeconds * (index - left)) : double.NegativeInfinity;
        double high = right > index ? _beats[right] - (MinGapSeconds * (right - index)) : double.PositiveInfinity;
        if (low > high)
            return false;
        seconds = Math.Clamp(seconds, low, high);
        if (seconds == _beats[index] && _pinned[index])
            return false;

        _beats[index] = seconds;
        _pinned[index] = true;
        for (int i = left + 1; i < index; i++)
            _beats[i] = _beats[left] + ((_beats[index] - _beats[left]) * (i - left) / (index - left));
        for (int i = index + 1; i < right; i++)
            _beats[i] = _beats[index] + ((_beats[right] - _beats[index]) * (i - index) / (right - index));
        _session.RaiseState();
        return true;
    }

    /// <summary>Puts a beat between each two: the proposal at twice the tempo, for a song proposed at half its tempo.</summary>
    public void Double()
    {
        if (!IsActive)
            return;
        var doubled = BeatMath.Doubled(Current());
        var pinned = new List<bool>(doubled.Beats.Count);
        for (int i = 0; i < doubled.Beats.Count; i++)
            pinned.Add(i % 2 == 0 && _pinned[i / 2]);
        Load(doubled, Downbeat * 2);
        _pinned = pinned;
        _session.RaiseState();
    }

    /// <summary>Keeps every other beat, those in step with the downbeat: the proposal at half the tempo.</summary>
    public void Halve()
    {
        if (!IsActive || _beats.Count < 4)
            return;
        int phase = Downbeat % 2;
        var halved = BeatMath.Halved(Current(), phase);
        var pinned = _pinned.Where((_, i) => i >= phase && (i - phase) % 2 == 0).ToList();
        Load(halved, (Downbeat - phase) / 2);
        _pinned = pinned;
        _session.RaiseState();
    }

    /// <summary>Moves the downbeat <paramref name="beats"/> beats later, or earlier when negative.</summary>
    public void ShiftDownbeat(int beats)
    {
        if (!IsActive)
            return;
        int shifted = Math.Clamp(Downbeat + beats, 0, _beats.Count - 1);
        if (shifted == Downbeat)
            return;
        Downbeat = shifted;
        _session.RaiseState();
    }

    /// <summary>Puts the downbeat on the beat nearest <paramref name="seconds"/>, as Find first downbeat finds it.</summary>
    public void SetDownbeatNear(double seconds)
    {
        if (!IsActive || BeatMath.NearestIndex(_beats, seconds, double.PositiveInfinity) is not (>= 0 and var index) || index == Downbeat)
            return;
        Downbeat = index;
        _session.RaiseState();
    }

    /// <summary>Takes back every adjustment: the proposal as it was found.</summary>
    public void Reset()
    {
        if (!IsActive)
            return;
        Load(_original, _originalDownbeat);
        _session.RaiseState();
    }

    /// <summary>Ends the run, changing nothing.</summary>
    public void Discard()
    {
        if (!IsActive)
            return;
        IsActive = false;
        _beats = [];
        _pinned = [];
        _unsure = [];
        _session.RaiseState();
    }

    /// <summary>The tempo map the proposal makes, worked out against the chart as it is now.</summary>
    public TempoEdit Edit(ProposalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsActive)
            return TempoEdit.Refused("There's no proposed tempo map to accept.");
        return ProposedTempoMap.Build(_session.Chart, _beats, _unsure, Downbeat, options, _session.Delay, _session.KeepTimes, Span);
    }

    /// <summary>Writes the proposed tempo map as one undoable step, and ends the run.</summary>
    /// <returns>Whether it was written; when it's refused, <see cref="EditorSession.Status"/> says why and the run goes on.</returns>
    public bool Accept(ProposalOptions options)
    {
        var edit = Edit(options);
        if (edit.IsRefused)
        {
            _session.Status = edit.Refusal;
            return false;
        }

        if (!_session.Execute(edit.Command!))
            return false;
        Discard();
        return true;
    }

    /// <summary>The proposal as it stands, with its adjustments.</summary>
    private BeatProposal Current()
    {
        var regions = _unsure.Select(u => new TempoRegion(u.First, u.Last, 0, 0)).ToList();
        return new BeatProposal([.. _beats], [.. _beats.Select(_ => 1f)], regions, Bpm, MayBeHalfTempo);
    }

    private void Load(BeatProposal proposal, int downbeat)
    {
        _beats = [.. proposal.Beats];
        _pinned = [.. proposal.Beats.Select(_ => false)];
        _unsure = [.. proposal.Regions.Where(static r => r.IsLowConfidence).Select(static r => (r.FirstBeat, r.LastBeat))];
        Downbeat = Math.Clamp(downbeat, 0, Math.Max(0, _beats.Count - 1));
        Bpm = proposal.Bpm;
        MayBeHalfTempo = proposal.MayBeHalfTempo;

        // The time signature where the proposal starts.
        long tick = (long)Math.Max(0, _session.TickAt(_beats.Count > 0 ? _beats[0] : 0));
        BeatsPerMeasure = Math.Max(1, _session.TempoMap.TimeSignatures.LastOrDefault(s => s.Tick <= tick, _session.TempoMap.TimeSignatures[0]).Numerator);
    }
}
