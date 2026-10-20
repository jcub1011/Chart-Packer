using System;
using Avalonia.Input;
using ChartShared.Core.Input;
using ChartShared.UI.Services;

namespace ChartBuilder.Services
{
    /// <summary>
    /// One of the instrument devices (charter-spec C19), for the calibration tool's input-device test (C11 item 2): its
    /// presses, timed as the device reported them with no offset taken off, and where its measured offset is stored.
    /// </summary>
    /// <param name="input">The app's instrument devices and their settings.</param>
    /// <param name="device">The device to measure.</param>
    public sealed class DeviceCalibrationInput(InputService input, InputDeviceInfo device) : ICalibrationInput
    {
        public string DeviceName => device.Name;

        public int OffsetMs => input.Settings.Settings.TuningOf(device.Id).OffsetMs;

        public bool IsKeyboard => device.Id.Kind == InputDeviceKind.Keyboard;

        /// <summary>Runs the devices while it listens, and hands on each press of this one (<see cref="InputService.ListenPresses"/>).</summary>
        public IDisposable Listen(Action<double> pressed)
        {
            ArgumentNullException.ThrowIfNull(pressed);
            return input.ListenPresses(id => id == device.Id, (e, _) => pressed(e.Time));
        }

        public void Key(PhysicalKey key, bool down) => input.PostKey(key, down);

        /// <summary>
        /// Stores the offset in the device's settings, keeping the rest, and has the playtest take it up; when the file can't
        /// be written, the device keeps the offset it had.
        /// </summary>
        public bool Save(int offsetMs)
        {
            var tuning = input.Settings.Settings.TuningOf(device.Id).Sanitized();
            tuning.OffsetMs = offsetMs;
            return input.SaveTuning(tuning);
        }
    }
}
