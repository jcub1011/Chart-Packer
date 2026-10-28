using ChartShared.Core.Charts;
using ChartShared.Core.Input;

namespace ChartShared.Core.Charting.Editing;

/// <summary>How a guitar records (charter-spec C19 item 7).</summary>
public enum GuitarRecordStyle
{
    /// <summary>A strum places the frets held, or an open note; letting go of a fret ends its sustain.</summary>
    Strum,

    /// <summary>Each fret places a note as it's pressed, as the number keys do.</summary>
    Frets,
}

/// <summary>
/// Record mode from a game controller or MIDI device (charter-spec C15 item 5, C19 item 7): timed actions placed as notes
/// in a <see cref="RecordPass"/>, each at the time its own event happened.
/// </summary>
/// <remarks>
/// <para>
/// Strumming, a fret is usually held until the next note, so it's a sustain only when held for at least
/// <see cref="EditableNote.MinimumSustainLength"/> ticks; shorter, and when the same fret is strummed again, it's a plain note.
/// </para>
/// <para>An open note is always a plain note, as there's no fret to hold it with.</para>
/// <para>Drums record by the pad's colour: a cymbal or a tom isn't recorded in v1.</para>
/// </remarks>
public sealed class RecordInput : IActionSink
{
    private readonly EditorSession _session;
    private readonly RecordPass _pass;
    private readonly GuitarRecordStyle _style;
    private readonly bool _dynamics;
    private readonly DrumsVariant _drums;
    private readonly bool _isDrums;
    private readonly Dictionary<int, double> _sounding = [];
    private int _frets;

    /// <param name="dynamicsFromVelocity">Whether a hard drum hit is an accent and a soft one a ghost.</param>
    public RecordInput(EditorSession session, RecordPass pass, GuitarRecordStyle style, bool dynamicsFromVelocity)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pass);
        _session = session;
        _pass = pass;
        _style = style;
        _dynamics = dynamicsFromVelocity;
        _isDrums = ChartInstruments.KindOf(pass.Track.Instrument) == ChartInstrumentKind.Drums;
        _drums = session.DrumsVariantShown;
    }

    public RecordPass Pass => _pass;

    /// <inheritdoc/>
    public void Handle(in TimedAction action)
    {
        if (_isDrums)
            Drum(action);
        else if (_style == GuitarRecordStyle.Frets)
            Fret(action);
        else
            Strum(action);
    }

    /// <summary>Lets go of everything held, as when the devices close.</summary>
    public void ReleaseAll(double song)
    {
        foreach (int lane in _sounding.Keys.ToList())
            Let(lane, song);
        _frets = 0;
    }

    private void Drum(in TimedAction action)
    {
        if (!action.IsDown || DrumPads.ShownLaneOf(action.Action, _drums) is not { } lane)
            return;
        var dynamics = !_dynamics ? NoteDynamics.None : action.Input.Strength switch
        {
            HitStrength.Accent => NoteDynamics.Accent,
            HitStrength.Ghost => NoteDynamics.Ghost,
            _ => NoteDynamics.None,
        };

        // A drum note has no length: it's let go at once, so the pad can be hit again.
        _pass.Press(lane, action.Song, dynamics: dynamics);
        _pass.Release(lane, action.Song);
    }

    private void Fret(in TimedAction action)
    {
        if (InstrumentActions.FretOf(action.Action) is not { } fret)
            return;
        if (action.IsDown)
        {
            if (_pass.Press(fret, action.Song))
                _sounding[fret] = action.Song;
        }
        else if (_sounding.Remove(fret))
        {
            // As a number key is: a sustain if held a grid step or more.
            _pass.Release(fret, action.Song);
        }
    }

    private void Strum(in TimedAction action)
    {
        if (InstrumentActions.FretOf(action.Action) is { } fret)
        {
            if (action.IsDown)
            {
                _frets |= 1 << fret;
            }
            else
            {
                _frets &= ~(1 << fret);
                Let(fret, action.Song);
            }

            return;
        }

        if (!InstrumentActions.IsStrum(action.Action) || !action.IsDown)
            return;

        // What still sounds from the last strum is a plain note: it was held only until this one.
        foreach (var (lane, at) in _sounding.ToList())
        {
            _pass.Release(lane, at);
            _sounding.Remove(lane);
        }

        // An open note has no fret to hold, so it's a plain note, let go at once.
        if (_frets == 0)
        {
            _pass.Press(ChartNote.OpenLane, action.Song);
            _pass.Release(ChartNote.OpenLane, action.Song);
            return;
        }

        for (int lane = 0; lane < 5; lane++)
        {
            if ((_frets & (1 << lane)) != 0 && _pass.Press(lane, action.Song))
                _sounding[lane] = action.Song;
        }
    }

    // Lets go of a lane: a sustain to now if it was held at least the minimum, else a plain note.
    private void Let(int lane, double song)
    {
        if (!_sounding.Remove(lane, out double from))
            return;
        double held = _session.TickAt(song) - _session.TickAt(from);
        _pass.Release(lane, held >= EditableNote.MinimumSustainLength ? song : from);
    }
}
