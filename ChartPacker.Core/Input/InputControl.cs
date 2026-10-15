namespace ChartPacker.Core.Input;

/// <summary>What kind of control on a device an input comes from.</summary>
public enum InputControlKind
{
    /// <summary>A key on the keyboard, by its physical place.</summary>
    Key,

    /// <summary>A controller's button.</summary>
    Button,

    /// <summary>A controller's axis: a stick, a trigger, a guitar's whammy bar or tilt.</summary>
    Axis,

    /// <summary>A controller's hat (d-pad), which a guitar's strum bar often is.</summary>
    Hat,

    /// <summary>A MIDI note: a drum pad, a key on a keyboard.</summary>
    MidiNote,

    /// <summary>A MIDI control change: a hi-hat pedal, a fader.</summary>
    MidiControl,
}

/// <summary>A control on a device.</summary>
/// <param name="Index">
/// Which one: a key's code from the app, a button, axis or hat number, a MIDI note or control number.
/// </param>
/// <param name="Direction">
/// For a binding: an axis's half, +1 or −1; a hat's direction, one of <see cref="HatDirections"/>. 0 for the rest, and for
/// a raw input, which carries the whole axis or hat.
/// </param>
public readonly record struct InputControl(InputControlKind Kind, int Index, int Direction = 0)
{
    /// <summary>The control without its direction: what a raw input from it carries.</summary>
    public InputControl Whole => this with { Direction = 0 };
}

/// <summary>A hat's directions, as bits of its value; up and right together is a diagonal.</summary>
public static class HatDirections
{
    public const int Up = 1;
    public const int Right = 2;
    public const int Down = 4;
    public const int Left = 8;

    /// <summary>The first direction set in <paramref name="hat"/>, from up clockwise; 0 when it's centred.</summary>
    public static int FirstOf(int hat)
    {
        foreach (int direction in (ReadOnlySpan<int>)[Up, Right, Down, Left])
        {
            if ((hat & direction) != 0)
                return direction;
        }

        return 0;
    }
}
