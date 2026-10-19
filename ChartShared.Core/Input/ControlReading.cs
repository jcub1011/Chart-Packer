namespace ChartShared.Core.Input;

/// <summary>
/// What a control's value means to a binding (charter-spec C19 item 2): whether it's pressed, and how far an axis or a
/// MIDI control has moved from where it rests. <see cref="InputRouter"/> plays by it; the Input devices window shows it.
/// </summary>
public static class ControlReading
{
    // An axis pressed is let go only once it's back inside this much of its threshold, so it doesn't chatter at the edge.
    private const float Hysteresis = 0.7f;

    /// <summary>Whether <paramref name="value"/> presses a control of <paramref name="kind"/>.</summary>
    /// <param name="direction">An axis's or MIDI control's way from rest (0 is up); a hat's <see cref="HatDirections"/> bit.</param>
    /// <param name="rest">Where an axis or MIDI control rests.</param>
    /// <param name="threshold">How far from rest an axis or MIDI control is pressed.</param>
    /// <param name="wasDown">Whether it was pressed: an axis then stays pressed until it's back inside the hysteresis.</param>
    public static bool IsPressed(InputControlKind kind, int direction, float rest, float threshold, float value, bool wasDown) => kind switch
    {
        InputControlKind.Axis or InputControlKind.MidiControl => Beyond(direction, rest, threshold, value, wasDown),
        InputControlKind.Hat => ((int)value & direction) != 0,
        InputControlKind.MidiNote => value > 0,
        _ => value >= 0.5f,
    };

    /// <summary>
    /// How far an axis or MIDI control has moved from <paramref name="rest"/> towards the end of its range in its
    /// direction, from 0 (at rest, or behind it) to 1 (at the end); 0 for any other control.
    /// </summary>
    public static float Travel(InputControlKind kind, int direction, float rest, float value)
    {
        float low = kind switch
        {
            InputControlKind.Axis => -1,
            InputControlKind.MidiControl => 0,
            _ => float.NaN,
        };
        if (float.IsNaN(low))
            return 0;

        float span = direction < 0 ? rest - low : 1 - rest;
        return span <= 0 ? 0 : Math.Clamp(Moved(direction, rest, value) / span, 0, 1);
    }

    // Whether an axis is past its threshold from where it rests, towards its direction; once pressed, it stays pressed
    // until it's back inside the hysteresis.
    private static bool Beyond(int direction, float rest, float threshold, float value, bool wasDown)
    {
        float at = Math.Max(0.05f, threshold);
        return Moved(direction, rest, value) >= (wasDown ? at * Hysteresis : at);
    }

    // How far a value is from where it rests, towards its direction.
    private static float Moved(int direction, float rest, float value) => (value - rest) * (direction == 0 ? 1 : Math.Sign(direction));
}
