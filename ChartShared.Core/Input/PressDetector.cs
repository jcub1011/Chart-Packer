namespace ChartShared.Core.Input;

/// <summary>
/// The presses of one device, for the calibration tool's input-device test (charter-spec C11 item 2): a key, a button or a
/// MIDI note pressed, or a hat pushed any way, as <see cref="BindingDetector"/> takes them. Letting go is no press, nor is a
/// key repeating while it's held: a control presses again only once it's been let go. Axes and MIDI controls never press,
/// so a whammy bar, a tilt or a pedal is no tap.
/// </summary>
public sealed class PressDetector
{
    private readonly InputDeviceId _device;
    private readonly int _minVelocity;
    private readonly HashSet<InputControl> _held = [];

    /// <param name="device">The device whose presses count.</param>
    /// <param name="minVelocity">The softest MIDI note that counts (<see cref="DeviceTuning.MinVelocity"/>).</param>
    public PressDetector(InputDeviceId device, int minVelocity = 1)
    {
        _device = device;
        _minVelocity = Math.Max(1, minVelocity);
    }

    /// <summary>Whether <paramref name="input"/> is a press of the device.</summary>
    public bool Feed(in RawInputEvent input)
    {
        if (input.Device != _device)
            return false;

        var control = input.Control.Whole;
        bool down;
        switch (control.Kind)
        {
            case InputControlKind.Key or InputControlKind.Button:
                down = input.Value >= 0.5f;
                break;
            case InputControlKind.Hat:
                down = HatDirections.FirstOf((int)input.Value) != 0;
                break;
            case InputControlKind.MidiNote:
                // A strike, never held: some kits send no note-off.
                return input.Velocity >= _minVelocity;
            default:
                return false;
        }

        if (!down)
        {
            _held.Remove(control);
            return false;
        }

        return _held.Add(control);
    }
}
