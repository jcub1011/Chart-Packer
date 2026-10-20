using System;
using Avalonia.Input;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Every input device the app has, for the calibration tool's audio tap test (charter-spec C11 item 1), from the app
    /// that has them: Chart Builder's instrument input. Everything is on the UI thread.
    /// </summary>
    public interface ICalibrationTaps
    {
        /// <summary>
        /// Starts listening to every device: <paramref name="tapped"/> is called for each press, with its time as the device
        /// reported it, in seconds on the input clock, and the device's stored offset in milliseconds, which isn't taken off.
        /// </summary>
        /// <returns>Stops listening.</returns>
        IDisposable Listen(Action<double, int> tapped);

        /// <summary>A key went down or up in the tool's window, for the keyboard.</summary>
        void Key(PhysicalKey key, bool down);
    }
}
