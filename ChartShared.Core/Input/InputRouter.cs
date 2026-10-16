namespace ChartShared.Core.Input;

/// <summary>An action pressed or let go (charter-spec C19 item 2), at the time it was, on <see cref="InputClock"/>.</summary>
/// <param name="Time">When, less the device's offset (<see cref="DeviceTuning.OffsetMs"/>).</param>
/// <param name="Strength">How hard, for a MIDI note; <see cref="HitStrength.Normal"/> otherwise.</param>
public readonly record struct ActionEvent(InstrumentAction Action, bool IsDown, double Time, InputDeviceId Device, HitStrength Strength = HitStrength.Normal);

/// <summary>
/// Turns what devices do into what a profile's bindings say they play (charter-spec C19 item 2): keys and buttons as
/// they're pressed, an axis as it moves past its threshold from where it rests, a hat by its directions, MIDI notes by
/// their velocity. One per profile, rebuilt when the bindings or the devices change.
/// </summary>
/// <remarks>
/// <para>
/// A fret, strum or other held action is pressed once however many of its controls are held, and let go when the last is.
/// A drum action is hit by every press of any of its controls, so two pedals bound to the kick are a double pedal.
/// </para>
/// <para>A key repeating as it's held is pressed once. A MIDI note-on with a velocity of 0 is a note-off.</para>
/// </remarks>
public sealed class InputRouter
{
    // An axis pressed is let go only once it's back inside this much of its threshold, so it doesn't chatter at the edge.
    private const float Hysteresis = 0.7f;

    private readonly Dictionary<(InputDeviceId, InputControlKind, int), List<Entry>> _entries = [];
    private readonly Dictionary<InputDeviceId, DeviceTuning> _tuning = [];
    private readonly Dictionary<InstrumentAction, int> _held = [];
    private readonly Dictionary<(InputDeviceId, int), double> _lastStrike = [];

    /// <param name="bindings">The profile's bindings.</param>
    /// <param name="tunings">The devices' saved settings; a device without is given the defaults.</param>
    /// <param name="connected">The devices connected now: each binding is found on one of these, by key, else by name.</param>
    /// <param name="keyIndex">A key's code, as the keyboard backend posts it, from its name; null for a name it doesn't know.</param>
    public InputRouter(IReadOnlyList<InputBinding> bindings, IReadOnlyList<DeviceTuning> tunings, IReadOnlyList<InputDeviceInfo> connected, Func<string, int?> keyIndex)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(tunings);
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(keyIndex);

        foreach (var tuning in tunings)
            _tuning[tuning.Device] = tuning.Sanitized();

        foreach (var binding in bindings)
        {
            if (!binding.IsComplete || DeviceOf(binding, connected) is not { } device)
                continue;
            int? index = binding.Control == InputControlKind.Key ? keyIndex(binding.Key!) : binding.Index;
            if (index is not { } at)
                continue;

            var key = (device, binding.Control, at);
            if (!_entries.TryGetValue(key, out var list))
                _entries[key] = list = [];
            list.Add(new Entry(binding.Action, binding.Control, binding.Direction, binding.Rest, binding.Threshold));
            Devices.Add(device);
        }
    }

    /// <summary>The devices the bindings were found on.</summary>
    public HashSet<InputDeviceId> Devices { get; } = [];

    /// <summary>Whether <paramref name="action"/> is held.</summary>
    public bool IsDown(InstrumentAction action) => _held.GetValueOrDefault(action) > 0;

    /// <summary>Whether <paramref name="control"/> on <paramref name="device"/> is bound to anything.</summary>
    public bool IsBound(InputDeviceId device, InputControl control) => _entries.ContainsKey((device, control.Kind, control.Index));

    /// <summary>Adds what <paramref name="input"/> presses or lets go to <paramref name="emit"/>.</summary>
    public void Handle(in RawInputEvent input, List<ActionEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (!_entries.TryGetValue((input.Device, input.Control.Kind, input.Control.Index), out var entries))
            return;

        var tuning = TuningOf(input.Device);
        double time = input.Time - (tuning.OffsetMs / 1000.0);
        var strength = HitStrength.Normal;
        if (input.Control.Kind == InputControlKind.MidiNote && input.Velocity > 0)
        {
            int velocity = input.Velocity;
            if (velocity < tuning.MinVelocity)
                return;
            var strike = (input.Device, input.Control.Index);
            if (_lastStrike.TryGetValue(strike, out double last) && time - last < tuning.RetriggerMs / 1000.0 && time >= last)
                return;
            _lastStrike[strike] = time;
            strength = tuning.StrengthOf(velocity);
        }

        foreach (var entry in entries)
        {
            bool down = IsDown(entry, input.Value);
            if (down && entry.Down && entry.Kind == InputControlKind.MidiNote)
            {
                // Struck again before its note-off: a hit of its own.
                Release(entry, time, input.Device, emit);
            }
            else if (down == entry.Down)
            {
                continue;
            }

            if (down)
                Press(entry, time, input.Device, strength, emit);
            else
                Release(entry, time, input.Device, emit);
        }
    }

    /// <summary>Lets go of every control held, as when the devices close or the window loses the keys.</summary>
    public void ReleaseAll(double time, List<ActionEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        foreach (var ((device, _, _), entries) in _entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Down)
                    Release(entry, time, device, emit);
            }
        }
    }

    /// <summary>Lets go of the keyboard's keys held, as when the window loses focus.</summary>
    public void ReleaseKeys(double time, List<ActionEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        foreach (var ((device, kind, _), entries) in _entries)
        {
            if (kind != InputControlKind.Key)
                continue;
            foreach (var entry in entries)
            {
                if (entry.Down)
                    Release(entry, time, device, emit);
            }
        }
    }

    // A binding's device among those connected: the one with its key and ordinal; else, as a driver or OS can give a
    // device another key, the one of its kind with its name, at its ordinal among those of that name. The keyboard is
    // always there.
    private static InputDeviceId? DeviceOf(InputBinding binding, IReadOnlyList<InputDeviceInfo> connected)
    {
        if (binding.DeviceKind == InputDeviceKind.Keyboard)
            return InputDeviceId.Keyboard;
        var device = binding.Device;
        if (connected.Any(d => d.Id == device))
            return device;
        if (string.IsNullOrEmpty(binding.DeviceName))
            return null;
        var named = connected.Where(d => d.Id.Kind == binding.DeviceKind && string.Equals(d.Name, binding.DeviceName, StringComparison.Ordinal)).ToList();
        return named.Count == 0 ? null : named[Math.Min(binding.Ordinal, named.Count - 1)].Id;
    }

    private static bool IsDown(Entry entry, float value) => entry.Kind switch
    {
        InputControlKind.Axis or InputControlKind.MidiControl => Beyond(entry, value),
        InputControlKind.Hat => ((int)value & entry.Direction) != 0,
        InputControlKind.MidiNote => value > 0,
        _ => value >= 0.5f,
    };

    // Whether an axis is past its threshold from where it rests, towards its direction; once pressed, it stays pressed
    // until it's back inside the hysteresis.
    private static bool Beyond(Entry entry, float value)
    {
        int direction = entry.Direction == 0 ? 1 : Math.Sign(entry.Direction);
        float moved = (value - entry.Rest) * direction;
        float threshold = Math.Max(0.05f, entry.Threshold);
        return moved >= (entry.Down ? threshold * Hysteresis : threshold);
    }

    private DeviceTuning TuningOf(InputDeviceId device)
    {
        if (!_tuning.TryGetValue(device, out var tuning))
            _tuning[device] = tuning = new DeviceTuning { Kind = device.Kind, Key = device.Key, Ordinal = device.Ordinal };
        return tuning;
    }

    private void Press(Entry entry, double time, InputDeviceId device, HitStrength strength, List<ActionEvent> emit)
    {
        entry.Down = true;
        int held = _held.GetValueOrDefault(entry.Action) + 1;
        _held[entry.Action] = held;
        if (held == 1 || InstrumentActions.IsDrumHit(entry.Action))
            emit.Add(new ActionEvent(entry.Action, true, time, device, strength));
    }

    private void Release(Entry entry, double time, InputDeviceId device, List<ActionEvent> emit)
    {
        entry.Down = false;
        int held = Math.Max(0, _held.GetValueOrDefault(entry.Action) - 1);
        _held[entry.Action] = held;
        if (held == 0)
            emit.Add(new ActionEvent(entry.Action, false, time, device));
    }

    private sealed class Entry(InstrumentAction action, InputControlKind kind, int direction, float rest, float threshold)
    {
        public InstrumentAction Action { get; } = action;

        public InputControlKind Kind { get; } = kind;

        public int Direction { get; } = direction;

        public float Rest { get; } = rest;

        public float Threshold { get; } = threshold;

        public bool Down { get; set; }
    }
}
