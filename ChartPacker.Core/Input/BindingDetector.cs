namespace ChartPacker.Core.Input;

/// <summary>A control <see cref="BindingDetector"/> found, with where it rests if it's an axis.</summary>
public readonly record struct DetectedControl(InputDeviceId Device, InputControl Control, float Rest);

/// <summary>
/// The Input devices window's <b>Bind</b> (charter-spec C19 item 2): takes the next control pressed, from any device. A key,
/// a button or a MIDI note pressed; a hat pushed one way; or an axis moved more than half way from where it rested.
/// Controls held when it starts are ignored until they're let go, so a resting pedal or a stuck axis isn't taken, and
/// nothing is taken in its first moment, which is the click that started it.
/// </summary>
public sealed class BindingDetector
{
    /// <summary>How long after it starts it takes nothing, in seconds.</summary>
    public const double SettleSeconds = 0.15;

    /// <summary>How far an axis must move from where it rests to be taken.</summary>
    public const float AxisTravel = 0.5f;

    private readonly double _from;
    private readonly int _minVelocity;
    private readonly IReadOnlyDictionary<(InputDeviceId, InputControl), float> _atStart;
    private readonly HashSet<(InputDeviceId, InputControl)> _held = [];

    /// <param name="start">When it starts, on <see cref="InputClock"/>.</param>
    /// <param name="atStart">Each control's value when it starts, where known (<see cref="InputHub.Snapshot"/>): where axes rest, and what's held.</param>
    /// <param name="minVelocity">The softest MIDI note taken.</param>
    public BindingDetector(double start, IReadOnlyDictionary<(InputDeviceId, InputControl), float> atStart, int minVelocity = 1)
    {
        ArgumentNullException.ThrowIfNull(atStart);
        _from = start + SettleSeconds;
        _atStart = atStart;
        _minVelocity = Math.Max(1, minVelocity);
        foreach (var (key, value) in atStart)
        {
            if (key.Item2.Kind is InputControlKind.Key or InputControlKind.Button or InputControlKind.Hat && value != 0)
                _held.Add(key);
        }
    }

    /// <summary>The control <paramref name="input"/> presses, if it's the one to bind; null otherwise.</summary>
    public DetectedControl? Feed(in RawInputEvent input)
    {
        var key = (input.Device, input.Control.Whole);
        var control = input.Control.Whole;
        if (control.Kind is InputControlKind.Axis or InputControlKind.MidiControl)
        {
            // An axis that hasn't moved since the devices opened rests in the middle; a MIDI control (a pedal) at 0.
            float rest = _atStart.TryGetValue(key, out float at) ? at : 0;
            float moved = input.Value - rest;
            if (input.Time < _from || Math.Abs(moved) < AxisTravel)
                return null;
            return new DetectedControl(input.Device, control with { Direction = Math.Sign(moved) }, rest);
        }

        int direction = control.Kind == InputControlKind.Hat ? HatDirections.FirstOf((int)input.Value) : 0;
        bool down = control.Kind switch
        {
            InputControlKind.Hat => direction != 0,
            InputControlKind.MidiNote => input.Velocity >= _minVelocity,
            _ => input.Value >= 0.5f,
        };
        if (!down)
        {
            _held.Remove(key);
            return null;
        }

        // A key held since before it started repeats; it's taken only once it's been let go and pressed again. A MIDI
        // note is a strike, never held: some kits send no note-off.
        if (_held.Contains(key))
            return null;
        if (input.Time < _from)
        {
            if (control.Kind != InputControlKind.MidiNote)
                _held.Add(key);
            return null;
        }

        return new DetectedControl(input.Device, control with { Direction = direction }, 0);
    }
}
