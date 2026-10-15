namespace ChartPacker.Core.Input;

/// <summary>
/// A MIDI input's short message, as a backend reads it (winmm's <c>MIM_DATA</c> packs it in a 32-bit value): what of it
/// is an input (charter-spec C19 item 1). Channels are ignored, as kits send on whichever they're set to.
/// </summary>
public static class MidiInput
{
    /// <summary>
    /// The input <paramref name="message"/> is, on <paramref name="device"/> at <paramref name="time"/>: a note-on or
    /// note-off, or a control change; null for anything else (clock, active sensing, pitch bend…).
    /// </summary>
    /// <param name="message">The status byte in the low byte, then the two data bytes, as winmm packs them.</param>
    public static RawInputEvent? Decode(uint message, InputDeviceId device, double time)
    {
        int status = (int)(message & 0xFF);
        int data1 = (int)((message >> 8) & 0x7F);
        int data2 = (int)((message >> 16) & 0x7F);
        return (status & 0xF0) switch
        {
            0x90 => new RawInputEvent(device, new InputControl(InputControlKind.MidiNote, data1), data2 / 127f, time),
            0x80 => new RawInputEvent(device, new InputControl(InputControlKind.MidiNote, data1), 0, time),
            0xB0 => new RawInputEvent(device, new InputControl(InputControlKind.MidiControl, data1), data2 / 127f, time),
            _ => null,
        };
    }
}

/// <summary>
/// Places a MIDI device's message times on <see cref="InputClock"/> (charter-spec C19 item 3). winmm stamps each message
/// in milliseconds since the port was started, on a clock of its own, so it's anchored to the input clock at the start and
/// kept honest against when messages arrive: a message can't have happened after it arrived, nor long before.
/// </summary>
/// <remarks>Belongs to one port, and its callback thread.</remarks>
public sealed class MidiTimestampAnchor
{
    /// <summary>The longest a message can take to arrive before its stamp is taken to have drifted.</summary>
    public const double MaxLag = 0.05;

    private double _start;

    /// <summary>The port was started at <paramref name="now"/>, on the input clock.</summary>
    public void Start(double now) => _start = now;

    /// <summary>When a message stamped <paramref name="milliseconds"/> happened, given that it arrived at <paramref name="arrived"/>.</summary>
    public double TimeOf(uint milliseconds, double arrived)
    {
        double time = _start + (milliseconds / 1000.0);
        if (time > arrived)
        {
            // winmm's clock runs ahead of the input clock: hold it back.
            _start -= time - arrived;
            return arrived;
        }

        if (arrived - time > MaxLag)
        {
            // It runs behind: bring it up, keeping the most a message is taken to have waited.
            _start += arrived - time - MaxLag;
            return arrived - MaxLag;
        }

        return time;
    }
}
