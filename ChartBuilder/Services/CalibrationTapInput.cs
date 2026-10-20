using System;
using Avalonia.Input;
using ChartShared.UI.Services;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Every instrument device (charter-spec C19), for the calibration tool's audio tap test (C11 item 1): each one's presses,
    /// timed as the device reported them, with the offset its own test measured (C11 item 2), which the tap test takes off.
    /// </summary>
    /// <param name="input">The app's instrument devices and their settings.</param>
    public sealed class CalibrationTapInput(InputService input) : ICalibrationTaps
    {
        /// <summary>Runs the devices while it listens, and hands on each press of any of them (<see cref="InputService.ListenPresses"/>).</summary>
        public IDisposable Listen(Action<double, int> tapped)
        {
            ArgumentNullException.ThrowIfNull(tapped);
            return input.ListenPresses(_ => true, (e, tuning) => tapped(e.Time, tuning.OffsetMs));
        }

        public void Key(PhysicalKey key, bool down) => input.PostKey(key, down);
    }
}
