using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Threading;
using ChartBuilder.Instruments;
using ChartShared.Core;
using ChartShared.Core.Input;

namespace ChartBuilder.Services
{
    /// <summary>
    /// The instrument devices and their bindings (charter-spec C19), one for the app: the keyboard and the backends, drained
    /// on the UI thread, and <c>input-bindings.json</c>.
    /// </summary>
    public sealed class InputService : IDisposable
    {
        private readonly IReadOnlyList<IInputSource> _backends;

        /// <param name="backends">The device backends besides the keyboard: SDL's controllers, and MIDI where there is any.</param>
        public InputService(InputSettingsService settings, IReadOnlyList<IInputSource> backends)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(backends);
            Settings = settings;
            _backends = backends;
            Keyboard = new KeyboardInputSource();
            InputHub? hub = null;
            hub = new InputHub([Keyboard, .. backends], () => Dispatcher.UIThread.Post(() => hub!.Drain(), DispatcherPriority.Input));
            Hub = hub;
        }

        /// <summary>Raised when the bindings or a device's settings change, so whatever plays takes them up.</summary>
        public event EventHandler? BindingsChanged;

        public InputHub Hub { get; }

        public KeyboardInputSource Keyboard { get; }

        public InputSettingsService Settings { get; }

        /// <summary>The backend that knows gamepad layouts, for the Xbox guitar preset; null when there's none.</summary>
        public IGamepadLayout? GamepadLayout => _backends.OfType<IGamepadLayout>().FirstOrDefault();

        /// <summary>A router for <paramref name="profile"/>'s bindings, over the devices connected now.</summary>
        public InputRouter RouterFor(InputProfileKind profile) =>
            new(Settings.Settings.BindingsOf(profile), Settings.Settings.Devices, Hub.Devices, KeyboardInputSource.IndexOf);

        /// <summary>Posts <paramref name="key"/> and drains it at once, so a key is judged as soon as it's handled.</summary>
        /// <returns>Whether it was posted: the backends are running.</returns>
        public bool PostKey(PhysicalKey key, bool down)
        {
            if (!Keyboard.Post(key, down))
                return false;
            Hub.Drain();
            return true;
        }

        /// <summary>
        /// Runs the devices until it's disposed, and hands on each press (<see cref="PressDetector"/>) of the devices
        /// <paramref name="includes"/> says, at the time the device reported it, with the device's settings.
        /// </summary>
        public IDisposable ListenPresses(Func<InputDeviceId, bool> includes, Action<RawInputEvent, DeviceTuning> pressed)
        {
            ArgumentNullException.ThrowIfNull(includes);
            ArgumentNullException.ThrowIfNull(pressed);

            var lease = Hub.Acquire();
            var presses = new Dictionary<InputDeviceId, PressDetector>();
            void OnReceived(IReadOnlyList<RawInputEvent> batch)
            {
                foreach (var e in batch)
                {
                    if (!includes(e.Device))
                        continue;

                    var tuning = Settings.Settings.TuningOf(e.Device);
                    if (!presses.TryGetValue(e.Device, out var detector))
                        presses[e.Device] = detector = new PressDetector(e.Device, tuning.MinVelocity);
                    if (detector.Feed(e))
                        pressed(e, tuning);
                }
            }

            Hub.Received += OnReceived;
            return new DisposeAction(() =>
            {
                Hub.Received -= OnReceived;
                lease.Dispose();
            });
        }

        /// <summary>Has the hub see <paramref name="key"/> pressed or let go, without playing it (<see cref="InputHub.Observe"/>).</summary>
        public void ObserveKey(PhysicalKey key, bool down) =>
            Hub.Observe(new RawInputEvent(InputDeviceId.Keyboard, KeyboardInputSource.ControlOf(key), down ? 1 : 0, InputClock.Now));

        /// <summary>Saves the bindings and has everything that plays take them up.</summary>
        public void SaveBindings()
        {
            Settings.Save();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Saves <paramref name="tuning"/> as its device's settings and has everything that plays take it up; when the file
        /// can't be written, puts back what the device had, so nothing changes.
        /// </summary>
        /// <returns>Whether it was saved.</returns>
        public bool SaveTuning(DeviceTuning tuning)
        {
            ArgumentNullException.ThrowIfNull(tuning);
            var settings = Settings.Settings;

            // SetTuning makes a new list, so this one is the devices' settings as they were.
            var before = settings.Devices;
            settings.SetTuning(tuning);
            if (!Settings.Save())
            {
                settings.Devices = before;
                return false;
            }

            BindingsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void Dispose() => Hub.Dispose();
    }
}
