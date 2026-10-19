using System;
using Avalonia.Input;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// The input device the calibration tool's input-device test (charter-spec C11 item 2) measures, from the app that has
    /// one: Chart Builder's instrument input. Everything is on the UI thread.
    /// </summary>
    public interface ICalibrationInput
    {
        /// <summary>The device's name, as the Input devices window shows it.</summary>
        string DeviceName { get; }

        /// <summary>The offset stored for the device, in milliseconds: how much later than it happens the device reports an input.</summary>
        int OffsetMs { get; }

        /// <summary>Whether the device is the keyboard, whose keys reach the tool's window and are handed on with <see cref="Key"/>.</summary>
        bool IsKeyboard { get; }

        /// <summary>
        /// Starts listening to the device: <paramref name="pressed"/> is called with the time of each press, as the device
        /// reported it, in seconds on the input clock, with no offset taken off.
        /// </summary>
        /// <returns>Stops listening.</returns>
        IDisposable Listen(Action<double> pressed);

        /// <summary>A key went down or up in the tool's window, for the keyboard.</summary>
        void Key(PhysicalKey key, bool down);

        /// <summary>Stores <paramref name="offsetMs"/> as the device's offset. Gives false when it couldn't be written.</summary>
        bool Save(int offsetMs);
    }
}
