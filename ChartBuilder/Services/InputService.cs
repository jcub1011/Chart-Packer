using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Threading;
using ChartBuilder.Instruments;
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
