using System;
using System.Collections.Generic;
using Avalonia.Input;
using ChartBuilder.Resources;
using ChartPacker.Core.Input;

namespace ChartBuilder.Instruments
{
    /// <summary>
    /// The keyboard as an instrument (charter-spec C19 item 1): the editor's canvas hands it the keys a playtest takes.
    /// Keys are matched by their physical place, as the editor's are, and saved by name.
    /// </summary>
    /// <remarks>
    /// Avalonia's key events carry no timestamp, so a key is timed when it's handled (charter-spec C15 item 5): the OS's
    /// message time is only as fine as its 15.6 ms tick on Windows, which would add more error than it took away.
    /// </remarks>
    public sealed class KeyboardInputSource : IInputSource
    {
        private IInputSink? _sink;

        /// <summary>The keyboard, as the Input devices window lists it.</summary>
        public static InputDeviceInfo Device { get; } = new(InputDeviceId.Keyboard, Strings.InputKeyboardName);

        public IReadOnlyList<InputDeviceInfo> Devices { get; } = [Device];

        /// <summary>The code a key is posted with, from its name; null for a name that isn't a key.</summary>
        public static int? IndexOf(string name) => Enum.TryParse<PhysicalKey>(name, out var key) && key != PhysicalKey.None ? (int)key : null;

        /// <summary>A key's name, as a binding saves it, from its code.</summary>
        public static string NameOf(int index) => ((PhysicalKey)index).ToString();

        /// <summary>The control <paramref name="key"/> is.</summary>
        public static InputControl ControlOf(PhysicalKey key) => new(InputControlKind.Key, (int)key);

        public void Start(IInputSink sink) => _sink = sink;

        public void Stop() => _sink = null;

        public void Rescan()
        {
        }

        public void Dispose() => _sink = null;

        /// <summary>Posts <paramref name="key"/> pressed or let go now; false when the backends aren't running.</summary>
        public bool Post(PhysicalKey key, bool down)
        {
            if (_sink is not { } sink)
                return false;
            sink.Post(new RawInputEvent(InputDeviceId.Keyboard, ControlOf(key), down ? 1 : 0, InputClock.Now));
            return true;
        }
    }
}
