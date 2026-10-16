using System.Numerics;
using ChartShared.Core.Input;

namespace ChartShared.Core.Charting.Playtest;

/// <summary>
/// The 5-fret rules (charter-spec C19 item 5): close to how the games play, to check a chart's HOPOs, taps, chords and
/// sustains play right. Each rule is its own method.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A single note is played with its fret held and none above it (anchoring); a chord with exactly its frets; an open
/// note with none (<see cref="Matches"/>).</item>
/// <item>A strum hits the first note in the window the frets match, missing any before it; with no match it waits
/// <see cref="FretRules.StrumLeniencyMs"/> for the frets, then is an overstrum (<see cref="Strum"/>).</item>
/// <item>A change of frets alone hits the next note if it's a tap, or a HOPO while the combo's going, and it wasn't
/// ghosted; a strum just after is part of that hit (<see cref="TryFretHit"/>).</item>
/// <item>A sustain needs its frets held to its end (<see cref="CheckSustains"/>).</item>
/// </list>
/// </remarks>
public sealed class FiveFretEngine : IPlaytestEngine
{
    private readonly HitWindow _window;
    private readonly FretRules _rules;
    private readonly NoteState[] _state;
    private readonly bool[] _ghosted;
    private readonly List<int> _sustains = [];
    private int _front;
    private int _frets;
    private double _speed = 1;
    private double? _strumAt;
    private double _absorbUntil = double.NegativeInfinity;

    public FiveFretEngine(PlaytestChart chart, HitWindow window, FretRules rules)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rules);
        Chart = chart;
        _window = window;
        _rules = rules;
        _state = new NoteState[chart.Notes.Count];
        _ghosted = new bool[chart.Notes.Count];
    }

    public PlaytestChart Chart { get; }

    public int Combo { get; private set; }

    /// <summary>The frets held, a bit each from green.</summary>
    public int HeldFrets => _frets;

    private IReadOnlyList<PlayNote> Notes => Chart.Notes;

    public void Reset(double song, double speed, Func<InstrumentAction, bool> isDown)
    {
        ArgumentNullException.ThrowIfNull(isDown);
        _speed = speed > 0 ? speed : 1;
        double late = _window.Late(_speed);
        for (int i = 0; i < _state.Length; i++)
            _state[i] = Notes[i].Time + late < song ? NoteState.Passed : NoteState.Open;
        Array.Clear(_ghosted);
        _front = Chart.IndexAtOrAfter(song - late);
        _frets = 0;
        foreach (var action in InstrumentActions.FiveFret)
        {
            if (InstrumentActions.FretOf(action) is { } fret && isDown(action))
                _frets |= 1 << fret;
        }

        _strumAt = null;
        _absorbUntil = double.NegativeInfinity;
        _sustains.Clear();
        Combo = 0;
    }

    public void Input(in TimedAction action, List<JudgeEvent> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (action.Speed > 0)
            _speed = action.Speed;
        double t = action.Song;
        AdvanceTo(t, output);

        if (InstrumentActions.FretOf(action.Action) is { } fret)
        {
            int before = _frets;
            _frets = action.IsDown ? _frets | (1 << fret) : _frets & ~(1 << fret);
            if (_frets != before)
                FretsChanged(fret, action.IsDown, t, output);
        }
        else if (InstrumentActions.IsStrum(action.Action) && action.IsDown)
        {
            Strum(t, output);
        }
    }

    public void Advance(double song, double speed, List<JudgeEvent> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (speed > 0)
            _speed = speed;
        AdvanceTo(song, output);
    }

    /// <summary>
    /// Whether the frets held play <paramref name="note"/>: a single note's fret and none above it, as frets below may
    /// be held; a chord's frets exactly; no fret for an open note.
    /// </summary>
    internal static bool Matches(PlayNote note, int frets)
    {
        if (note.IsOpen)
            return frets == 0;
        if (note.IsChord)
            return frets == note.Mask;
        int top = 31 - BitOperations.LeadingZeroCount((uint)note.Mask);
        return (frets & note.Mask) != 0 && frets >> (top + 1) == 0;
    }

    private double Ms(double milliseconds) => milliseconds / 1000 * _speed;

    private void FretsChanged(int fret, bool pressed, double t, List<JudgeEvent> output)
    {
        CheckSustains(t, output);

        // A strum waiting for the frets hits as soon as they're right.
        if (_strumAt is not null && TryStrumHit(t, output))
        {
            _strumAt = null;
            return;
        }

        if (TryFretHit(t, output))
        {
            // A strum just before is part of the hit.
            _strumAt = null;
            return;
        }

        if (pressed && _rules.AntiGhosting)
            Ghost(fret, t, output);
    }

    /// <summary>A strum: part of a HOPO or tap just hit, else a hit, else waiting for the frets.</summary>
    private void Strum(double t, List<JudgeEvent> output)
    {
        if (t <= _absorbUntil)
        {
            _absorbUntil = double.NegativeInfinity;
            return;
        }

        // A strum still waiting when another comes was an overstrum.
        if (_strumAt is { } waiting)
        {
            _strumAt = null;
            Overstrum(waiting, output);
        }

        if (!TryStrumHit(t, output))
            _strumAt = t;
    }

    /// <summary>A strum hits the first open note in the window that the frets play, and misses the open ones before it.</summary>
    private bool TryStrumHit(double t, List<JudgeEvent> output)
    {
        double early = _window.Early(_speed), late = _window.Late(_speed);
        for (int i = _front; i < Notes.Count && Notes[i].Time - early <= t; i++)
        {
            var note = Notes[i];
            if (_state[i] != NoteState.Open || note.Time + late < t || !Matches(note, _frets))
                continue;
            MissBefore(i, output);
            Hit(i, t, output);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The frets alone hit the next open note in the window: a tap at any time, a HOPO while the combo's going, unless a
    /// wrong fret ghosted it.
    /// </summary>
    private bool TryFretHit(double t, List<JudgeEvent> output)
    {
        if (NextInWindow(t) is not { } i)
            return false;
        var note = Notes[i];
        bool byFrets = note.Kind == PlayNoteKind.Tap || (note.Kind == PlayNoteKind.Hopo && (Combo > 0 || !_rules.HopoNeedsCombo));
        if (!byFrets || _ghosted[i] || !Matches(note, _frets))
            return false;
        Hit(i, t, output);
        _absorbUntil = t + Ms(_rules.HopoLeniencyMs);
        return true;
    }

    /// <summary>
    /// A fret pressed that's wrong for the next HOPO or tap in the window ghosts it: it must be strummed. A fret of a
    /// chord, or below a single note's (an anchor), isn't wrong.
    /// </summary>
    private void Ghost(int fret, double t, List<JudgeEvent> output)
    {
        if (NextInWindow(t) is not { } i || _ghosted[i])
            return;
        var note = Notes[i];
        if (note.Kind is not (PlayNoteKind.Hopo or PlayNoteKind.Tap))
            return;
        int bit = 1 << fret;
        bool wrong = note.IsOpen || (note.IsChord ? (note.Mask & bit) == 0 : bit > note.Mask);
        if (!wrong)
            return;
        _ghosted[i] = true;
        output.Add(new JudgeEvent(JudgeKind.Ghost, i, t, 0, note.Lanes[0], Combo));
    }

    /// <summary>A sustain whose frets aren't held any more ends: dropped if that's too early, else done.</summary>
    private void CheckSustains(double t, List<JudgeEvent> output)
    {
        for (int s = _sustains.Count - 1; s >= 0; s--)
        {
            var note = Notes[_sustains[s]];
            if (Matches(note, _frets))
                continue;
            _sustains.RemoveAt(s);
            output.Add(t < note.EndTime - Ms(_rules.SustainDropLeniencyMs)
                ? new JudgeEvent(JudgeKind.SustainDrop, note.Index, t, (t - note.EndTime) / _speed * 1000, note.Lanes[0], Combo)
                : new JudgeEvent(JudgeKind.SustainDone, note.Index, t, 0, note.Lanes[0], Combo));
        }
    }

    private void Hit(int i, double t, List<JudgeEvent> output)
    {
        var note = Notes[i];
        _state[i] = NoteState.Hit;
        Combo++;
        output.Add(new JudgeEvent(JudgeKind.Hit, i, t, (t - note.Time) / _speed * 1000, note.Lanes[0], Combo));

        // A note hit ends the sustains before it: extended sustains aren't judged in v1.
        foreach (int s in _sustains)
            output.Add(new JudgeEvent(JudgeKind.SustainDone, s, t, 0, Notes[s].Lanes[0], Combo));
        _sustains.Clear();
        if (note.HasSustain)
            _sustains.Add(i);
    }

    private void MissBefore(int i, List<JudgeEvent> output)
    {
        for (int j = _front; j < i; j++)
        {
            if (_state[j] == NoteState.Open)
                Miss(j, output);
        }
    }

    private void Miss(int i, List<JudgeEvent> output)
    {
        _state[i] = NoteState.Missed;
        Combo = 0;
        output.Add(new JudgeEvent(JudgeKind.Miss, i, Notes[i].Time, 0, Notes[i].Lanes[0], Combo));
    }

    private void Overstrum(double at, List<JudgeEvent> output)
    {
        Combo = 0;
        output.Add(new JudgeEvent(JudgeKind.Overstrum, -1, at, 0, -2, Combo));
    }

    // The first open note whose window is open at t.
    private int? NextInWindow(double t)
    {
        double early = _window.Early(_speed), late = _window.Late(_speed);
        for (int i = _front; i < Notes.Count && Notes[i].Time - early <= t; i++)
        {
            if (_state[i] == NoteState.Open && Notes[i].Time + late >= t)
                return i;
        }

        return null;
    }

    // What time alone settles: a strum that waited too long, sustains reaching their ends, notes whose window closed.
    private void AdvanceTo(double t, List<JudgeEvent> output)
    {
        if (_strumAt is { } strum && t > strum + Ms(_rules.StrumLeniencyMs))
        {
            _strumAt = null;
            Overstrum(strum, output);
        }

        if (t > _absorbUntil)
            _absorbUntil = double.NegativeInfinity;

        for (int s = _sustains.Count - 1; s >= 0; s--)
        {
            var note = Notes[_sustains[s]];
            if (t < note.EndTime)
                continue;
            _sustains.RemoveAt(s);
            output.Add(new JudgeEvent(JudgeKind.SustainDone, note.Index, note.EndTime, 0, note.Lanes[0], Combo));
        }

        double late = _window.Late(_speed);
        for (; _front < Notes.Count; _front++)
        {
            if (_state[_front] == NoteState.Open)
            {
                if (Notes[_front].Time + late >= t)
                    break;
                Miss(_front, output);
            }
        }
    }
}

/// <summary>Where a note is in a pass.</summary>
internal enum NoteState : byte
{
    /// <summary>Not judged yet.</summary>
    Open,

    Hit,
    Missed,

    /// <summary>Passed over unjudged, by a seek or the loop.</summary>
    Passed,
}
