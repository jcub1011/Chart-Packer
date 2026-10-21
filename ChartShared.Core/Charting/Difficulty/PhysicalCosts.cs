namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// The costs of a note to the first two attributes, the physical ones (docs/difficulty-model.md §4.1, §4.2 and §14):
/// strum and note for frets, kick and hands for drums. One goes with each <see cref="DifficultyState"/>, and remembers what
/// it needs of the notes before.
/// </summary>
internal abstract class PhysicalCosts
{
    /// <summary>The costs <paramref name="parameters"/>' instrument has, before any note.</summary>
    public static PhysicalCosts For(DifficultyParameters parameters) =>
        parameters.Instrument.IsDrums() ? new DrumCosts(parameters) : new FretCosts(parameters);

    /// <summary>
    /// What <paramref name="note"/>, after every note given so far, costs the first and the second attribute, before
    /// they're normalized; it's then remembered.
    /// </summary>
    /// <param name="guarded">The interval since the note before, no shorter than the shortest counted.</param>
    /// <param name="first">Whether it's the first note.</param>
    public abstract (double First, double Second) Cost(in DifficultyNote note, double guarded, bool first);

    /// <summary>
    /// The move <paramref name="note"/> is for the local entropy (§4.5): what tells it apart from other moves, as one
    /// value.
    /// </summary>
    /// <param name="previousLanes">The lanes of the note before.</param>
    /// <param name="iota">The interval from the note before in 48ths of a beat, capped; −1 for the first note.</param>
    public abstract long MoveToken(in DifficultyNote note, byte previousLanes, int iota, bool first);

    /// <summary>A copy to carry on from, which this one's later notes don't change.</summary>
    public virtual PhysicalCosts Clone() => (PhysicalCosts)MemberwiseClone();
}

/// <summary>
/// Five-fret and six-fret costs: the strumming hand (§4.1) and the fretting hand (§4.2), six-fret's moves on its grid of
/// columns (<see cref="GridTransitionCost"/>).
/// </summary>
internal sealed class FretCosts(DifficultyParameters parameters) : PhysicalCosts
{
    private readonly DifficultyParameters _p = parameters;
    private readonly bool _grid = parameters.Instrument == DifficultyInstrument.SixFret;
    private byte _previousLanes;
    private NoteKind _previousKind;
    private double _lastStrum = double.NaN;

    public override (double First, double Second) Cost(in DifficultyNote note, double guarded, bool first)
    {
        double strum = StrumCost(note, guarded, first);
        double fret = NoteCost(note, first ? 0 : 1000 / guarded, first);
        _previousLanes = note.Lanes;
        _previousKind = note.Kind;
        return (strum, fret);
    }

    public override long MoveToken(in DifficultyNote note, byte previousLanes, int iota, bool first)
    {
        int count = note.Count;
        int move = first ? -1 : (int)Math.Round(60 * (Centre(note.Lanes) - Centre(previousLanes)));
        int span = _grid ? GridTransitionCost.Span(note.Lanes) : TransitionCost.Span(note.Lanes);
        return ((((((long)move + 1024) * 8 + count) * 8 + span) * 4) + (int)note.Kind) * 4096 + iota + 1;
    }

    private double Centre(byte lanes) => _grid ? GridTransitionCost.Centre(lanes, _p) : TransitionCost.Centre(lanes, _p);

    // §4.1: the strummed rate, faster still past alternate strumming, strummed chords, and switching from a HOPO or tap.
    private double StrumCost(in DifficultyNote note, double guarded, bool first)
    {
        if (note.Kind != NoteKind.Strum)
            return 0;
        double sinceStrum = double.IsNaN(_lastStrum) ? double.PositiveInfinity : note.TimeMs - _lastStrum;
        _lastStrum = note.TimeMs;
        double cost = 0;
        if (!double.IsInfinity(sinceStrum))
        {
            double strumGuarded = Math.Max(sinceStrum, _p.MinIntervalMs);
            double rate = 1000 / strumGuarded;
            double alternate = Math.Pow(Math.Max(1, _p.AlternateStrumMs / strumGuarded), _p.AlternateStrumPower);
            double chord = note.Count > 1 ? _p.StrummedChordFactor : 1;
            cost = _p.StrumScale * Math.Pow(rate, _p.StrumSpeedPower) * alternate * chord;
        }

        if (!first && _previousKind is NoteKind.Hopo or NoteKind.Tap)
            cost += _p.SwitchCost * Math.Max(0, (_p.SwitchWindowMs / guarded) - 1);
        return cost;
    }

    // §4.2: the transition from the note before, sounding the same frets again without a strum, and lanes held.
    private double NoteCost(in DifficultyNote note, double rate, bool first)
    {
        if (first)
            return 0;
        double transition = _grid ? GridTransitionCost.Between(_previousLanes, note.Lanes, _p) : TransitionCost.Between(_previousLanes, note.Lanes, _p);
        double refret = _previousLanes == note.Lanes && note.Lanes != 0 && note.Kind != NoteKind.Strum ? _p.RefretCost : 0;
        double kind = note.Kind == NoteKind.Strum ? 1 : _p.HopoTapFactor;
        return _p.NoteScale * Math.Pow(rate, _p.NoteSpeedPower) * kind * (transition + refret + (_p.HeldLaneCost * note.Held));
    }
}
