using System;
using Avalonia.Threading;
using ChartShared.Audio.Playback;
using ChartShared.Core.Settings;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// The audio/visual latency of the device an app is playing on (charter-spec C10 item 5, C11): what's drawn
    /// against the audio is drawn this much earlier, times the speed. One for the app, used on the UI thread.
    /// </summary>
    /// <param name="file">The shared latency file, which the calibration tool writes.</param>
    public sealed class AudioLatencyService(AudioLatencyFile file)
    {
        /// <summary>The latency file, for the calibration tool.</summary>
        public AudioLatencyFile File => file;

        /// <summary>The device last played on, or null before anything has played.</summary>
        public AudioDevice? Device { get; private set; }

        /// <summary>The device's latency, in milliseconds; 0 before anything has played.</summary>
        public int Milliseconds { get; private set; }

        /// <summary>Where <see cref="Milliseconds"/> came from; <see cref="LatencySource.Fallback"/> for a device never calibrated.</summary>
        public LatencySource Source { get; private set; } = LatencySource.Fallback;

        /// <summary>Raised on the UI thread when <see cref="Device"/> or <see cref="Milliseconds"/> changes.</summary>
        public event EventHandler? Changed;

        /// <summary>Follows <paramref name="output"/>'s device as it moves, loading each one's latency (AC C11.3).</summary>
        /// <returns>Stops following it.</returns>
        public IDisposable Follow(IAudioOutput output)
        {
            ArgumentNullException.ThrowIfNull(output);

            void OnDeviceChanged(AudioDevice device) => Dispatcher.UIThread.Post(() => SetDevice(device));
            output.DeviceChanged += OnDeviceChanged;
            SetDevice(output.Device);
            return new Unsubscriber(() => output.DeviceChanged -= OnDeviceChanged);
        }

        /// <summary>Loads <paramref name="device"/>'s latency.</summary>
        public void SetDevice(AudioDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            Device = device;
            Reload();
        }

        /// <summary>Reads the device's latency again, as after the calibration tool saved one.</summary>
        public void Reload()
        {
            if (Device is not { } device)
                return;

            var found = file.FindAndRemember(device.Name, device.Id);
            Milliseconds = found.Milliseconds;
            Source = found.Source;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private sealed class Unsubscriber(Action unsubscribe) : IDisposable
        {
            private Action? _unsubscribe = unsubscribe;

            public void Dispose()
            {
                _unsubscribe?.Invoke();
                _unsubscribe = null;
            }
        }
    }
}
