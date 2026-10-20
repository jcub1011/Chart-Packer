using System;
using System.Collections.Generic;
using Avalonia.Input;
using ChartShared.Core;
using ChartShared.Core.Input;
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
        /// <summary>Runs the devices while it listens, and hands on each press of any of them (<see cref="PressDetector"/>).</summary>
        public IDisposable Listen(Action<double, int> tapped)
        {
            ArgumentNullException.ThrowIfNull(tapped);

            var lease = input.Hub.Acquire();
            var presses = new Dictionary<InputDeviceId, PressDetector>();
            void OnReceived(IReadOnlyList<RawInputEvent> batch)
            {
                foreach (var e in batch)
                {
                    var tuning = input.Settings.Settings.TuningOf(e.Device);
                    if (!presses.TryGetValue(e.Device, out var detector))
                        presses[e.Device] = detector = new PressDetector(e.Device, tuning.MinVelocity);
                    if (detector.Feed(e))
                        tapped(e.Time, tuning.OffsetMs);
                }
            }

            input.Hub.Received += OnReceived;
            return new DisposeAction(() =>
            {
                input.Hub.Received -= OnReceived;
                lease.Dispose();
            });
        }

        public void Key(PhysicalKey key, bool down) => input.PostKey(key, down);
    }
}
