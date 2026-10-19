using System;
using System.Collections.Generic;
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

        /// <summary>Runs the devices while it listens, and hands on each press of this one (<see cref="PressDetector"/>).</summary>
        public IDisposable Listen(Action<double> pressed)
        {
            ArgumentNullException.ThrowIfNull(pressed);

            var lease = input.Hub.Acquire();
            var presses = new PressDetector(device.Id, input.Settings.Settings.TuningOf(device.Id).MinVelocity);
            void OnReceived(IReadOnlyList<RawInputEvent> batch)
            {
                foreach (var e in batch)
                {
                    if (presses.Feed(e))
                        pressed(e.Time);
                }
            }

            input.Hub.Received += OnReceived;
            return new Listening(() =>
            {
                input.Hub.Received -= OnReceived;
                lease.Dispose();
            });
        }

        public void Key(PhysicalKey key, bool down) => input.PostKey(key, down);

        /// <summary>Stores the offset in the device's settings, keeping the rest, and has the playtest take it up.</summary>
        public bool Save(int offsetMs)
        {
            var tuning = input.Settings.Settings.TuningOf(device.Id).Sanitized();
            tuning.OffsetMs = offsetMs;
            input.Settings.Settings.SetTuning(tuning);
            input.SaveBindings();
            return true;
        }

        private sealed class Listening(Action stop) : IDisposable
        {
            private Action? _stop = stop;

            public void Dispose()
            {
                _stop?.Invoke();
                _stop = null;
            }
        }
    }
}
