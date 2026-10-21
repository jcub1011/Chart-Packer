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
    // An n-gram of phrase tokens, twenty bits each, so up to six to a key.
    private const int MaxPhraseLength = 6;
    private const int PhraseTokenBits = 20;

    // The token for "no note before" (⊥).
    private const int None = -1;

    private readonly DifficultyModel _model;
    private readonly DifficultyParameters _p;

    // The first two attributes' costs, the instrument's own.
    private PhysicalCosts _costs;

    private int _count;
    private double _previousTime;

    // The rhythm's and patterns' history: the notes outside roll and trill lanes, which they read as rests (§14.1).
    private int _heard;
    private double _heardTime;
    private double _previousInterval = double.PositiveInfinity;
    private double _previousBeat;
    private byte _previousLanes;
    private SubdivisionFamily _previousFamily;

    // The beats of the notes within the last half-beat or so, for syncopation.
    private List<double> _recentBeats = [];

    // The last moves, newest at _count - 1 (mod the window), for the local entropy.
    private long[] _moves;
    private readonly long[] _entropyScratch;

    // The last phrase tokens, newest at _count - 1 (mod the phrase length), and when each phrase was last seen.
    private uint[] _phraseTokens;
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
        _phraseTokens = new uint[_p.PhraseLength];
        _costs = PhysicalCosts.For(_p);
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
        copy._phraseTokens = (uint[])_phraseTokens.Clone();
        copy._lastSeen = new Dictionary<UInt128, double>(_lastSeen);
        copy._costs = _costs.Clone();
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
        var (strum, fret) = _costs.Cost(note, guarded, first);

        // A roll or trill lane is played however the player likes, so its timing and patterns cost nothing, and the
        // rhythm and patterns read it as a rest: what's after it is timed from the note before it (§14.1).
        double rhythm = 0, pattern = 0;
        if (note.Kind != NoteKind.Roll)
        {
            bool firstHeard = _heard == 0;
            double heardInterval = firstHeard ? double.PositiveInfinity : note.TimeMs - _heardTime;
            double rate = firstHeard ? 0 : 1000 / Math.Max(heardInterval, _p.MinIntervalMs);
            rhythm = RhythmCost(note, heardInterval, rate, firstHeard);
            pattern = PatternCost(note, rate, firstHeard);
            _previousInterval = heardInterval;
            _heardTime = note.TimeMs;
            _previousBeat = note.Beat;
            _previousLanes = note.Lanes;
            _heard++;
        }

        var levels = Levels;
        Levels = new DifficultyAttributes(
            levels.Strum + (strum * _model.StrumScale),
            levels.Note + (fret * _model.NoteScale),
            levels.Rhythm + (rhythm * _model.RhythmScale),
            levels.Pattern + (pattern * _model.PatternScale),
            levels.Endurance);

        _previousTime = note.TimeMs;
        _count++;
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
        _moves[_heard % _moves.Length] = _costs.MoveToken(note, _previousLanes, iota, first);

        int window = Math.Min(_heard + 1, _moves.Length);
        double entropy = window < 2 ? 0 : Entropy(window) / Math.Log2(window);

        // Phrase tokens keep the lanes, so a phrase is only seen again when it truly is: eight bits of lanes, two of kind
        // and nine of interval.
        _phraseTokens[_heard % _phraseTokens.Length] = (uint)(note.Lanes | ((int)note.Kind << 8) | (Math.Min(iota + 1, 511) << 10));
        double novelty = 1;
        if (_heard + 1 >= _phraseTokens.Length)
        {
            UInt128 key = 0;
            for (int k = _phraseTokens.Length - 1; k >= 0; k--)
                key = (key << PhraseTokenBits) | _phraseTokens[(_heard - k) % _phraseTokens.Length];
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
            scratch[k] = _moves[(_heard - k) % _moves.Length];
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
