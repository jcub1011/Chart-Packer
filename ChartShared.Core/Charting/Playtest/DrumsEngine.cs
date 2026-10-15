using ChartShared.Core.Charts;
using ChartShared.Core.Input;

namespace ChartShared.Core.Charting.Playtest;

/// <summary>
/// The drums rules (charter-spec C19 item 6): each note is hit by its own pad within the window, the earliest first; a pad
/// hit with no note to hit is an overhit. On Pro drums a cymbal note needs a cymbal and a tom note a tom, for the colours
/// the bindings split (<see cref="CymbalSplit"/>); with one control for a colour, either is a hit.
/// </summary>
public sealed class DrumsEngine : IPlaytestEngine
{
    private readonly HitWindow _window;
    private readonly DrumRules _rules;
    private readonly CymbalSplit _split;
    private readonly NoteState[] _state;
    private int _front;
    private double _speed = 1;

    /// <param name="split">The colours whose tom and cymbal the drums profile binds apart.</param>
    public DrumsEngine(PlaytestChart chart, HitWindow window, DrumRules rules, CymbalSplit split)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rules);
        Chart = chart;
        _window = window;
        _rules = rules;
        _split = split;
        _state = new NoteState[chart.Notes.Count];
    }

    public PlaytestChart Chart { get; }

    public int Combo { get; private set; }

    private IReadOnlyList<PlayNote> Notes => Chart.Notes;

    public void Reset(double song, double speed, Func<InstrumentAction, bool> isDown)
    {
        _speed = speed > 0 ? speed : 1;
        double late = _window.Late(_speed);
        for (int i = 0; i < _state.Length; i++)
            _state[i] = Notes[i].Time + late < song ? NoteState.Passed : NoteState.Open;
        _front = Chart.IndexAtOrAfter(song - late);
        Combo = 0;
    }

    public void Input(in TimedAction action, List<JudgeEvent> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (action.Speed > 0)
            _speed = action.Speed;
        double t = action.Song;
        AdvanceTo(t, output);
        if (!action.IsDown || DrumPads.Of(action.Action, Chart.Drums) is not { } pad)
            return;

        if (FirstHittable(pad, t) is { } i)
        {
            var note = Notes[i];
            _state[i] = NoteState.Hit;
            Combo++;
            output.Add(new JudgeEvent(JudgeKind.Hit, i, t, (t - note.Time) / _speed * 1000, note.Lane, Combo));
            if (_rules.JudgeDynamics && !DynamicsMatch(note, action.Input.Strength))
                output.Add(new JudgeEvent(JudgeKind.WrongDynamics, i, t, 0, note.Lane, Combo));
            return;
        }

        if (_rules.OverhitBreaksCombo)
            Combo = 0;
        output.Add(new JudgeEvent(JudgeKind.Overhit, -1, t, 0, pad.Lane, Combo));
    }

    public void Advance(double song, double speed, List<JudgeEvent> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (speed > 0)
            _speed = speed;
        AdvanceTo(song, output);
    }

    /// <summary>Whether a pad hit as <paramref name="pad"/> plays <paramref name="note"/>'s lane, and, where it matters, its cymbal or tom.</summary>
    private bool Plays(PlayNote note, DrumPad pad)
    {
        if (note.Lane != pad.Lane)
            return false;
        bool strict = Chart.Drums == DrumsVariant.Pro && _rules.CymbalsMustMatch && (_split & DrumPads.ColourOf(note.Lane)) != 0;
        return !strict || note.Cymbal == pad.Cymbal;
    }

    /// <summary>An accent needs a hard hit and a ghost a soft one; other notes any.</summary>
    private static bool DynamicsMatch(PlayNote note, HitStrength strength)
    {
        if (note.Flags.HasFlag(NoteFlags.Accent))
            return strength == HitStrength.Accent;
        if (note.Flags.HasFlag(NoteFlags.Ghost))
            return strength == HitStrength.Ghost;
        return true;
    }

    // The earliest open note in the window at t that the pad plays.
    private int? FirstHittable(DrumPad pad, double t)
    {
        double early = _window.Early(_speed), late = _window.Late(_speed);
        for (int i = _front; i < Notes.Count && Notes[i].Time - early <= t; i++)
        {
            if (_state[i] == NoteState.Open && Notes[i].Time + late >= t && Plays(Notes[i], pad))
                return i;
        }

        return null;
    }

    private void AdvanceTo(double t, List<JudgeEvent> output)
    {
        double late = _window.Late(_speed);
        for (; _front < Notes.Count; _front++)
        {
            if (_state[_front] != NoteState.Open)
                continue;
            if (Notes[_front].Time + late >= t)
                break;
            _state[_front] = NoteState.Missed;
            Combo = 0;
            output.Add(new JudgeEvent(JudgeKind.Miss, _front, Notes[_front].Time, 0, Notes[_front].Lane, Combo));
        }
    }
}
