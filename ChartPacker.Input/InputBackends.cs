using ChartPacker.Core.Input;
using ChartPacker.Input.Midi;
using ChartPacker.Input.Sdl;

namespace ChartPacker.Input;

/// <summary>The device backends this platform has (charter-spec C19 item 1): SDL's controllers everywhere, MIDI on Windows.</summary>
public static class InputBackends
{
    /// <summary>The backends, not started: they start when something plays them, on the main thread.</summary>
    public static IReadOnlyList<IInputSource> Create()
    {
        var backends = new List<IInputSource> { new SdlJoystickSource() };
        if (OperatingSystem.IsWindows())
            backends.Add(new WinmmMidiSource());
        return backends;
    }
}
