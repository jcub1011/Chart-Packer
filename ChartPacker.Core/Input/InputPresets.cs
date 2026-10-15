namespace ChartPacker.Core.Input;

/// <summary>A control of a standard gamepad layout, as SDL maps a controller it knows (an Xbox 360 guitar is one).</summary>
public enum GamepadControl
{
    South,
    East,
    West,
    North,
    LeftShoulder,
    RightShoulder,
    Back,
    Start,
    DpadUp,
    DpadDown,
    RightStickX,
    RightStickY,
}

/// <summary>A backend that can say which of a device's raw controls a standard gamepad control is.</summary>
public interface IGamepadLayout
{
    /// <summary>The raw control <paramref name="control"/> is on <paramref name="device"/>, with its direction; null when the device hasn't it, or isn't a gamepad SDL knows.</summary>
    InputControl? ControlOf(InputDeviceId device, GamepadControl control);
}

/// <summary>Ready-made bindings (charter-spec C19 item 2).</summary>
public static class InputPresets
{
    /// <summary>The keyboard's 5-fret defaults: the number keys for the frets, as record mode's, and Enter and the up and down arrows to strum.</summary>
    public static List<InputBinding> KeyboardFiveFret() =>
    [
        InputBinding.OfKey(InstrumentAction.Green, "Digit1"),
        InputBinding.OfKey(InstrumentAction.Red, "Digit2"),
        InputBinding.OfKey(InstrumentAction.Yellow, "Digit3"),
        InputBinding.OfKey(InstrumentAction.Blue, "Digit4"),
        InputBinding.OfKey(InstrumentAction.Orange, "Digit5"),
        InputBinding.OfKey(InstrumentAction.StrumDown, "Enter"),
        InputBinding.OfKey(InstrumentAction.StrumDown, "ArrowDown"),
        InputBinding.OfKey(InstrumentAction.StrumUp, "ArrowUp"),
    ];

    /// <summary>
    /// The keyboard's drums defaults: 0 the kick, as record mode's, and 1 to 5 the pads across. Each colour has one key, so
    /// it hits a cymbal or a tom alike. On 4 pads, 4 and 5 are both green; on 5-lane, 4 is orange and 5 green, as the lanes
    /// are drawn.
    /// </summary>
    public static List<InputBinding> KeyboardDrums() =>
    [
        InputBinding.OfKey(InstrumentAction.Kick, "Digit0"),
        InputBinding.OfKey(InstrumentAction.RedPad, "Digit1"),
        InputBinding.OfKey(InstrumentAction.YellowCymbal, "Digit2"),
        InputBinding.OfKey(InstrumentAction.BlueTom, "Digit3"),
        InputBinding.OfKey(InstrumentAction.OrangePad, "Digit4"),
        InputBinding.OfKey(InstrumentAction.GreenTom, "Digit5"),
    ];

    /// <summary>
    /// The General MIDI drum map's notes, and the notes most electronic kits send for their extra zones (hi-hat edges, a
    /// floor tom's rim), by the action they play. The hi-hat pedal's chick (44) is left out, so closing the hi-hat isn't a hit.
    /// </summary>
    public static IReadOnlyList<(int Note, InstrumentAction Action)> GeneralMidiDrumNotes { get; } =
    [
        (35, InstrumentAction.Kick), (36, InstrumentAction.Kick),
        (37, InstrumentAction.RedPad), (38, InstrumentAction.RedPad), (40, InstrumentAction.RedPad),
        (42, InstrumentAction.YellowCymbal), (46, InstrumentAction.YellowCymbal), (22, InstrumentAction.YellowCymbal), (26, InstrumentAction.YellowCymbal),
        (48, InstrumentAction.YellowTom), (50, InstrumentAction.YellowTom),
        (45, InstrumentAction.BlueTom), (47, InstrumentAction.BlueTom),
        (41, InstrumentAction.GreenTom), (43, InstrumentAction.GreenTom), (58, InstrumentAction.GreenTom),
        (51, InstrumentAction.BlueCymbal), (53, InstrumentAction.BlueCymbal), (59, InstrumentAction.BlueCymbal),
        (49, InstrumentAction.GreenCymbal), (52, InstrumentAction.GreenCymbal), (55, InstrumentAction.GreenCymbal), (57, InstrumentAction.GreenCymbal),
    ];

    /// <summary>The General MIDI drum map on <paramref name="device"/> (<see cref="GeneralMidiDrumNotes"/>).</summary>
    public static List<InputBinding> GeneralMidiDrums(InputDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return [.. GeneralMidiDrumNotes.Select(n => InputBinding.Of(n.Action, device, new InputControl(InputControlKind.MidiNote, n.Note)))];
    }

    /// <summary>
    /// An Xbox 360 guitar's usual layout on <paramref name="device"/>: frets on A, B, Y, X and the left shoulder, the strum
    /// bar on the d-pad, whammy on the right stick's X and star power on Back. Controls the device hasn't are left out.
    /// </summary>
    public static List<InputBinding> GamepadGuitar(InputDeviceInfo device, IGamepadLayout layout)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layout);
        (GamepadControl, InstrumentAction)[] map =
        [
            (GamepadControl.South, InstrumentAction.Green),
            (GamepadControl.East, InstrumentAction.Red),
            (GamepadControl.North, InstrumentAction.Yellow),
            (GamepadControl.West, InstrumentAction.Blue),
            (GamepadControl.LeftShoulder, InstrumentAction.Orange),
            (GamepadControl.DpadUp, InstrumentAction.StrumUp),
            (GamepadControl.DpadDown, InstrumentAction.StrumDown),
            (GamepadControl.RightStickX, InstrumentAction.Whammy),
            (GamepadControl.Back, InstrumentAction.StarPower),
        ];

        var bindings = new List<InputBinding>();
        foreach (var (control, action) in map)
        {
            if (layout.ControlOf(device.Id, control) is { } raw)
                bindings.Add(InputBinding.Of(action, device, raw));
        }

        return bindings;
    }
}
