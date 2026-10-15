namespace ChartShared.Core.Input;

/// <summary>Where an instrument input comes from (charter-spec C19 item 1).</summary>
public enum InputDeviceKind
{
    /// <summary>The computer's keyboard, while the editor has focus.</summary>
    Keyboard,

    /// <summary>A game controller read through SDL's joystick subsystem: a gamepad, a guitar or a drum controller.</summary>
    Controller,

    /// <summary>A MIDI input port: a drum kit, a pad controller or a keyboard.</summary>
    Midi,
}

/// <summary>What a device says it is, where it says so; only a hint for presets, as many devices don't.</summary>
public enum InputDeviceHint
{
    Unknown,
    Gamepad,
    Guitar,
    DrumKit,
}

/// <summary>
/// A device, as bindings find it again (AC C19.3): its kind and a key stable across reconnecting it, with an ordinal to
/// tell apart two of the same device connected together.
/// </summary>
/// <param name="Key">
/// SDL's GUID for a controller, which comes from the device's bus, vendor and product rather than the port it's in; a MIDI
/// port's name; <c>keyboard</c> for the keyboard.
/// </param>
/// <param name="Ordinal">Which of the devices connected with this key, in the order the backend lists them: 0 for the first.</param>
public readonly record struct InputDeviceId(InputDeviceKind Kind, string Key, int Ordinal = 0)
{
    /// <summary>The keyboard: there's only ever one, whatever's plugged in.</summary>
    public static InputDeviceId Keyboard { get; } = new(InputDeviceKind.Keyboard, "keyboard");
}

/// <summary>Why a device connected can't be read.</summary>
public enum InputDeviceProblem
{
    None,

    /// <summary>Another app holds it, as a MIDI port can be held by one app at a time.</summary>
    InUse,

    /// <summary>It couldn't be opened for another reason.</summary>
    CantOpen,
}

/// <summary>A device connected now, as a backend lists it.</summary>
/// <param name="Name">What the device calls itself, for the user and to find bindings by when its key has changed.</param>
/// <param name="Problem">Why it can't be read, if it can't.</param>
public sealed record InputDeviceInfo(InputDeviceId Id, string Name, InputDeviceHint Hint = InputDeviceHint.Unknown, InputDeviceProblem Problem = InputDeviceProblem.None);
