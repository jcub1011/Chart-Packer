namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// The difficulty model's state as it reads a track note by note (docs/difficulty-model.md §3, §4 and §8.1): each
/// attribute's normalized strain, and what each needs of the notes before to score the next.
/// </summary>
/// <remarks>
/// Online: what it holds at a time depends only on the notes up to then. The phrase history is the only part that
/// grows, by at most a phrase a note. <see cref="Clone"/> takes a snapshot to carry on from.
/// </remarks>
internal sealed class DifficultyState
{
    // An n-gram of phrase tokens, sixteen bits each, so up to eight to a key.
    private const int MaxPhraseLength = 8;

    // The token for "no note before" (⊥).
    private const int None = -1;

    private readonly DifficultyModel _model;
    private readonly DifficultyParameters _p;

    private int _count;
    private double _previousTime;
    private double _previousInterval = double.PositiveInfinity;
    private double _previousBeat;
    private byte _previousLanes;
    private NoteKind _previousKind;
    private double _lastStrum = double.NaN;
    private SubdivisionFamily _previousFamily;

    // The beats of the notes within the last half-beat or so, for syncopation.
    private List<double> _recentBeats = [];

    // The last moves, newest at _count - 1 (mod the window), for the local entropy.
    private long[] _moves;
    private readonly long[] _entropyScratch;

    // The last phrase tokens, newest at _count - 1 (mod the phrase length), and when each phrase was last seen.
    private ushort[] _phraseTokens;
    private Dictionary<UInt128, double> _lastSeen = [];

    public DifficultyState(DifficultyModel model)
    {
        _model = model;
        _p = model.Parameters;
        if (_p.PhraseLength is < 1 or > MaxPhraseLength)
            throw new ArgumentOutOfRangeException(nameof(model), _p.PhraseLength, $"A phrase is from 1 to {MaxPhraseLength} notes.");
        if (_p.EntropyWindow < 1)
            throw new ArgumentOutOfRangeException(nameof(model), _p.EntropyWindow, "The entropy window is at least one note.");
        _moves = new long[_p.EntropyWindow];
        _entropyScratch = new long[_p.EntropyWindow];
        _phraseTokens = new ushort[_p.PhraseLength];
    }

    /// <summary>The time <see cref="Levels"/> is at, in milliseconds; NaN before the first note or time.</summary>
    public double TimeMs { get; private set; } = double.NaN;

    /// <summary>Each attribute's normalized strain, and endurance, at <see cref="TimeMs"/>.</summary>
    public DifficultyAttributes Levels { get; private set; }

    /// <summary>How many notes it has read.</summary>
    public int Count => _count;

    /// <summary>A copy to carry on from, which this one's later notes don't change.</summary>
    public DifficultyState Clone()
    {
        var copy = (DifficultyState)MemberwiseClone();
        copy._recentBeats = [.. _recentBeats];
        copy._moves = (long[])_moves.Clone();
        copy._phraseTokens = (ushort[])_phraseTokens.Clone();
        copy._lastSeen = new Dictionary<UInt128, double>(_lastSeen);
        return copy;
    }

    /// <summary>Moves on to <paramref name="timeMs"/> with no note: strains decay and endurance builds or recovers.</summary>
    public void AdvanceTo(double timeMs)
    {
        if (double.IsNaN(TimeMs))
            TimeMs = timeMs;
        else if (timeMs > TimeMs)
        {
            Levels = _model.Integrate(Levels, timeMs - TimeMs);
            TimeMs = timeMs;
        }
    }

    /// <summary>Reads <paramref name="note"/>, which is after every note read so far: each attribute adds its cost.</summary>
    public void Advance(in DifficultyNote note)
    {
        AdvanceTo(note.TimeMs);
        bool first = _count == 0;
        double interval = first ? double.PositiveInfinity : note.TimeMs - _previousTime;
        double guarded = Math.Max(interval, _p.MinIntervalMs);
        double rate = first ? 0 : 1000 / guarded;

        double strum = StrumCost(note, guarded, first);
        double fret = NoteCost(note, rate, first);
        double rhythm = RhythmCost(note, interval, rate, first);
        double pattern = PatternCost(note, rate, first);
        var levels = Levels;
        Levels = new DifficultyAttributes(
            levels.Strum + (strum * _model.StrumScale),
            levels.Note + (fret * _model.NoteScale),
            levels.Rhythm + (rhythm * _model.RhythmScale),
            levels.Pattern + (pattern * _model.PatternScale),
            levels.Endurance);

        _previousInterval = interval;
        _previousTime = note.TimeMs;
        _previousBeat = note.Beat;
        _previousLanes = note.Lanes;
        _previousKind = note.Kind;
        _count++;
    }

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
        double transition = TransitionCost.Between(_previousLanes, note.Lanes, _p);
        double refret = _previousLanes == note.Lanes && note.Lanes != 0 && note.Kind != NoteKind.Strum ? _p.RefretCost : 0;
        double kind = note.Kind == NoteKind.Strum ? 1 : _p.HopoTapFactor;
        return _p.NoteScale * Math.Pow(rate, _p.NoteSpeedPower) * kind * (transition + refret + (_p.HeldLaneCost * note.Held));
    }

    // §4.3: the change of interval, of subdivision family, and syncopation.
    private double RhythmCost(in DifficultyNote note, double interval, double rate, bool first)
    {
        var family = RatioComplexity.FamilyOf(note.Beat, _p);
        double ratio = first ? 0 : RatioComplexity.ChangeCost(interval, _previousInterval, _p);
        bool familyChange = !first && family != _previousFamily && family != SubdivisionFamily.Neutral && _previousFamily != SubdivisionFamily.Neutral;

        // Syncopation: off the beat and half-beat, with no earlier note on the last beat or half-beat.
        double beat = note.Beat;
        double anchor = Math.Floor(2 * beat) / 2;
        double tolerance = _p.BeatTolerance;
        int stale = 0;
        while (stale < _recentBeats.Count && _recentBeats[stale] < beat - 0.5 - tolerance)
            stale++;
        _recentBeats.RemoveRange(0, stale);
        bool anchored = false;
        foreach (double b in _recentBeats)
            anchored |= Math.Abs(b - anchor) < tolerance;
        bool syncopated = family != SubdivisionFamily.Neutral && !anchored;
        _recentBeats.Add(beat);
        _previousFamily = family;
        if (first)
            return 0;
        double cost = ratio + (familyChange ? _p.FamilyChangeCost : 0) + (syncopated ? _p.SyncopationCost : 0);
        return _p.RhythmScale * Math.Pow(rate, _p.RhythmSpeedPower) * cost;
    }

    // §4.5: how varied the recent moves are, and how long since this phrase was last played.
    private double PatternCost(in DifficultyNote note, double rate, bool first)
    {
        int iota = first ? None : (int)Math.Min(Math.Round(48 * (note.Beat - _previousBeat)), 48.0 * _p.RestBeats);
        int count = note.Count;
        int move = first ? None : (int)Math.Round(60 * (TransitionCost.Centre(note.Lanes, _p) - TransitionCost.Centre(_previousLanes, _p)));
        int span = TransitionCost.Span(note.Lanes);
        long moveToken = ((((((long)move + 1024) * 8 + count) * 8 + span) * 4) + (int)note.Kind) * 4096 + iota + 1;
        _moves[_count % _moves.Length] = moveToken;

        int window = Math.Min(_count + 1, _moves.Length);
        double entropy = window < 2 ? 0 : Entropy(window) / Math.Log2(window);

        // Phrase tokens keep the lanes, so a phrase is only seen again when it truly is: five bits of lanes, two of kind
        // and nine of interval.
        _phraseTokens[_count % _phraseTokens.Length] = (ushort)(note.Lanes | ((int)note.Kind << 5) | (Math.Min(iota + 1, 511) << 7));
        double novelty = 1;
        if (_count + 1 >= _phraseTokens.Length)
        {
            UInt128 key = 0;
            for (int k = _phraseTokens.Length - 1; k >= 0; k--)
                key = (key << 16) | _phraseTokens[(_count - k) % _phraseTokens.Length];
            if (_lastSeen.TryGetValue(key, out double last))
                novelty = 1 - Math.Exp(-(note.TimeMs - last) / _p.RecognitionMs);
            _lastSeen[key] = note.TimeMs;
        }

        return _p.PatternScale * Math.Pow(rate, _p.PatternSpeedPower) * ((_p.EntropyWeight * entropy) + (_p.NoveltyWeight * novelty));
    }

    // The Shannon entropy, in bits, of the last window moves.
    private double Entropy(int window)
    {
        var scratch = _entropyScratch.AsSpan(0, window);
        for (int k = 0; k < window; k++)
            scratch[k] = _moves[(_count - k) % _moves.Length];
        scratch.Sort();
        double entropy = 0;
        for (int start = 0; start < window;)
        {
            int end = start + 1;
            while (end < window && scratch[end] == scratch[start])
                end++;
            double share = (double)(end - start) / window;
            entropy -= share * Math.Log2(share);
            start = end;
        }

        return entropy;
    }
}
