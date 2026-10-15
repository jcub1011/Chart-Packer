using System.Diagnostics;

namespace ChartPacker.Core.Input;

/// <summary>
/// One change to one control of a device, as its backend read it (charter-spec C19 item 3), before bindings say what it
/// plays.
/// </summary>
/// <param name="Control">The control, without a direction (<see cref="InputControl.Whole"/>).</param>
/// <param name="Value">
/// A key or button: 1 down, 0 up. An axis: −1 to 1. A hat: its <see cref="HatDirections"/> bits. A MIDI note: its
/// velocity over 127, 0 for a note-off. A MIDI control: its value over 127.
/// </param>
/// <param name="Time">When it happened, in seconds on <see cref="InputClock"/>: the event's own timestamp where it has one.</param>
public readonly record struct RawInputEvent(InputDeviceId Device, InputControl Control, float Value, double Time)
{
    /// <summary>A MIDI note's velocity, 0 to 127, from <see cref="Value"/>.</summary>
    public int Velocity => (int)Math.Round(Math.Clamp(Value, 0, 1) * 127);
}

/// <summary>
/// The one monotonic clock every input is timed on (charter-spec C19 item 3): <see cref="Stopwatch"/>'s, which is also the
/// clock <see cref="TimeProvider.System"/> gives the audio player, so input and playback times compare directly.
/// </summary>
public static class InputClock
{
    /// <summary>Now, in seconds.</summary>
    public static double Now => Seconds(Stopwatch.GetTimestamp());

    /// <summary>A <see cref="Stopwatch"/> timestamp in seconds.</summary>
    public static double Seconds(long timestamp) => timestamp / (double)Stopwatch.Frequency;
}

/// <summary>Where a backend puts what it reads. Safe to call from any thread.</summary>
public interface IInputSink
{
    /// <summary>A control changed.</summary>
    void Post(in RawInputEvent input);

    /// <summary>A device was connected or disconnected, or one became readable or not.</summary>
    void DevicesChanged();
}

/// <summary>
/// One backend's devices: the keyboard, SDL's controllers, or MIDI ports (charter-spec C19 item 1). Started only while
/// something uses its devices, so a port is free for other apps the rest of the time.
/// </summary>
public interface IInputSource : IDisposable
{
    /// <summary>The devices connected now. Safe to read from any thread.</summary>
    IReadOnlyList<InputDeviceInfo> Devices { get; }

    /// <summary>Opens the devices and starts posting what they do to <paramref name="sink"/>. On the main thread.</summary>
    void Start(IInputSink sink);

    /// <summary>Closes the devices and stops posting. On the main thread.</summary>
    void Stop();

    /// <summary>Looks for devices connected or disconnected, for backends the OS doesn't tell. On the main thread.</summary>
    void Rescan();
}
