using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Threading;
using ChartBuilder.Instruments;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Input;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A device in the Input devices window: what it is, how busy it is now, and its own settings.</summary>
    public sealed partial class InputDeviceRow : ObservableObject
    {
        private readonly InputDevicesViewModel _owner;
        private bool _loading;

        internal InputDeviceRow(InputDevicesViewModel owner, InputDeviceInfo info, DeviceTuning tuning)
        {
            _owner = owner;
            Info = info;
            _loading = true;
            OffsetMs = tuning.OffsetMs;
            MinVelocity = tuning.MinVelocity;
            RetriggerMs = tuning.RetriggerMs;
            AccentVelocity = tuning.AccentVelocity;
            GhostVelocity = tuning.GhostVelocity;
            _loading = false;
        }

        public InputDeviceInfo Info { get; }

        public string Name => Info.Name;

        /// <summary>What kind of device it is, and why it can't be read, if it can't.</summary>
        public string Detail => Info.Problem switch
        {
            InputDeviceProblem.InUse => Strings.InputDeviceInUse,
            InputDeviceProblem.CantOpen => Strings.InputDeviceCantOpen,
            _ => Info.Id.Kind switch
            {
                InputDeviceKind.Keyboard => Strings.InputDeviceKindKeyboard,
                InputDeviceKind.Midi => Strings.InputDeviceKindMidi,
                _ => Info.Hint switch
                {
                    InputDeviceHint.Guitar => Strings.InputDeviceKindGuitar,
                    InputDeviceHint.DrumKit => Strings.InputDeviceKindDrums,
                    _ => Strings.InputDeviceKindController,
                },
            },
        };

        public bool HasProblem => Info.Problem != InputDeviceProblem.None;

        public bool IsMidi => Info.Id.Kind == InputDeviceKind.Midi;

        public bool IsController => Info.Id.Kind == InputDeviceKind.Controller;

        /// <summary>How recently the device did anything, from 1 (now) to 0.</summary>
        [ObservableProperty]
        public partial double Activity { get; set; }

        [ObservableProperty]
        public partial decimal? OffsetMs { get; set; }

        [ObservableProperty]
        public partial decimal? MinVelocity { get; set; }

        [ObservableProperty]
        public partial decimal? RetriggerMs { get; set; }

        [ObservableProperty]
        public partial decimal? AccentVelocity { get; set; }

        [ObservableProperty]
        public partial decimal? GhostVelocity { get; set; }

        [RelayCommand]
        private void UseGeneralMidi() => _owner.UseGeneralMidi(this);

        /// <summary>Opens the calibration tool to measure the device's offset (charter-spec C11 item 2).</summary>
        [RelayCommand]
        private Task CalibrateAsync() => _owner.CalibrateAsync(this);

        /// <summary>Shows <paramref name="offsetMs"/>, as the calibration tool saved it, without saving it again.</summary>
        internal void ShowOffset(int offsetMs)
        {
            _loading = true;
            OffsetMs = offsetMs;
            _loading = false;
        }

        [RelayCommand]
        private void UseGuitarLayout() => _owner.UseGuitarLayout(this);

        partial void OnOffsetMsChanged(decimal? value) => Save();

        partial void OnMinVelocityChanged(decimal? value) => Save();

        partial void OnRetriggerMsChanged(decimal? value) => Save();

        partial void OnAccentVelocityChanged(decimal? value) => Save();

        partial void OnGhostVelocityChanged(decimal? value) => Save();

        private void Save()
        {
            if (_loading)
                return;
            var defaults = new DeviceTuning();
            _owner.SaveTuning(new DeviceTuning
            {
                Kind = Info.Id.Kind,
                Key = Info.Id.Key,
                Ordinal = Info.Id.Ordinal,
                OffsetMs = (int)(OffsetMs ?? defaults.OffsetMs),
                MinVelocity = (int)(MinVelocity ?? defaults.MinVelocity),
                RetriggerMs = (int)(RetriggerMs ?? defaults.RetriggerMs),
                AccentVelocity = (int)(AccentVelocity ?? defaults.AccentVelocity),
                GhostVelocity = (int)(GhostVelocity ?? defaults.GhostVelocity),
            });
        }
    }

    /// <summary>A control bound to an action, as a chip beside it.</summary>
    /// <param name="IsConnected">Whether its device is connected now.</param>
    public sealed record InputBindingChip(InputProfileKind Profile, InputBinding Binding, string Label, string ToolTip, bool IsConnected);

    /// <summary>An action of a profile, with the controls bound to it.</summary>
    public sealed partial class InputActionRow(InputProfileKind profile, InstrumentAction action, string label) : ObservableObject
    {
        public InputProfileKind Profile { get; } = profile;

        public InstrumentAction Action { get; } = action;

        public string Label { get; } = label;

        [ObservableProperty]
        public partial IReadOnlyList<InputBindingChip> Chips { get; set; } = [];

        /// <summary>Whether Bind is waiting for this action's control.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BindText))]
        public partial bool IsListening { get; set; }

        public string BindText => IsListening ? Strings.InputBindListening : Strings.InputBindButton;
    }

    /// <summary>A profile's tab: its actions.</summary>
    public sealed record InputProfileTab(InputProfileKind Kind, string Header, IReadOnlyList<InputActionRow> Actions);

    /// <summary>A control Bind found that another action has: it's moved only once that's confirmed.</summary>
    public sealed record PendingInputMove(InputProfileKind Profile, InputBinding Binding, InputBinding Other);

    /// <summary>
    /// The Input devices window (charter-spec C19 item 2): the devices connected, each with a meter of what it's doing and
    /// its own settings, and each profile's actions with the controls bound to them. <b>Bind</b> takes the next control
    /// pressed. The devices run while the window is open.
    /// </summary>
    public sealed partial class InputDevicesViewModel : ViewModelBase, IDisposable
    {
        // How long a device's meter takes to fall from full to nothing after it last did something, in seconds.
        private const double MeterFall = 0.4;

        private readonly InputService _input;
        private readonly Func<InputDeviceInfo, Task<bool>>? _calibrate;
        private readonly IDisposable _lease;
        private readonly DispatcherTimer _meters;
        private BindingDetector? _detector;
        private InputActionRow? _listening;
        private bool _disposed;

        /// <param name="calibrate">
        /// Opens the calibration tool to measure a device's offset, and waits for it to close; gives false when the tool was
        /// open already, so the device wasn't measured.
        /// </param>
        public InputDevicesViewModel(InputService input, Func<InputDeviceInfo, Task<bool>>? calibrate = null)
        {
            _input = input;
            _calibrate = calibrate;
            _lease = input.Hub.Acquire();
            Profiles =
            [
                new InputProfileTab(InputProfileKind.FiveFret, Strings.InputProfileFiveFret, RowsOf(InputProfileKind.FiveFret)),
                new InputProfileTab(InputProfileKind.Drums, Strings.InputProfileDrums, RowsOf(InputProfileKind.Drums)),
            ];
            input.Hub.Received += OnReceived;
            input.Hub.DevicesChanged += OnDevicesChanged;
            input.BindingsChanged += OnBindingsChanged;
            _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateMeters());
            _meters.Start();
            RefreshDevices();
            RefreshChips();
        }

        public ObservableCollection<InputDeviceRow> Devices { get; } = [];

        public IReadOnlyList<InputProfileTab> Profiles { get; }

        /// <summary>What the window has to say: what Bind did, or why it didn't.</summary>
        [ObservableProperty]
        public partial string? Status { get; private set; }

        /// <summary>A control Bind found that another action has, waiting to be moved or not.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingMove))]
        public partial PendingInputMove? PendingMove { get; private set; }

        public bool HasPendingMove => PendingMove is not null;

        /// <summary>Whether Bind is waiting for a control.</summary>
        public bool IsListening => _listening is not null;

        /// <summary>
        /// A key pressed or let go in the window: while Bind waits, it's the control to bind, and Esc stops waiting. Space,
        /// Esc, P and N are the editor's and can't be bound.
        /// </summary>
        /// <returns>Whether the key is taken.</returns>
        public bool HandleKey(PhysicalKey key, bool down)
        {
            if (_listening is null)
                return false;
            if (down && key == PhysicalKey.Escape)
            {
                StopListening();
                Status = null;
                return true;
            }

            if (key is PhysicalKey.Space or PhysicalKey.Escape or PhysicalKey.P or PhysicalKey.N)
            {
                if (down)
                    Status = Strings.InputKeyReserved;
                return true;
            }

            _input.PostKey(key, down);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _meters.Stop();
            _input.Hub.Received -= OnReceived;
            _input.Hub.DevicesChanged -= OnDevicesChanged;
            _input.BindingsChanged -= OnBindingsChanged;
            _lease.Dispose();
        }

        /// <summary>Saves a device's own settings.</summary>
        internal void SaveTuning(DeviceTuning tuning)
        {
            _input.Settings.Settings.SetTuning(tuning);
            _input.SaveBindings();
        }

        /// <summary>
        /// Measures a device's offset with the calibration tool, then shows what it saved, if anything. Bind stops waiting
        /// first, so the test's taps aren't taken for a control to bind.
        /// </summary>
        internal async Task CalibrateAsync(InputDeviceRow row)
        {
            if (_calibrate is null)
                return;

            StopListening();
            PendingMove = null;
            Status = null;
            bool ran = await _calibrate(row.Info);
            if (_disposed)
                return;

            if (ran)
                row.ShowOffset(_input.Settings.Settings.TuningOf(row.Info.Id).OffsetMs);
            else
                Status = Strings.InputCalibrationAlreadyOpen;
        }

        /// <summary>The General MIDI drum map on a MIDI device, in place of the drums bindings it had.</summary>
        internal void UseGeneralMidi(InputDeviceRow row)
        {
            var drums = _input.Settings.Settings.Drums;
            drums.RemoveAll(b => b.Device == row.Info.Id);
            drums.AddRange(InputPresets.GeneralMidiDrums(row.Info));
            _input.SaveBindings();
            Status = string.Format(CultureInfo.CurrentCulture, Strings.InputPresetAppliedFormat, Strings.InputPresetGeneralMidi, row.Name);
        }

        /// <summary>An Xbox 360 guitar's layout on a controller, in place of the 5-fret bindings it had.</summary>
        internal void UseGuitarLayout(InputDeviceRow row)
        {
            var bindings = _input.GamepadLayout is { } layout ? InputPresets.GamepadGuitar(row.Info, layout) : [];
            if (bindings.Count == 0)
            {
                Status = Strings.InputPresetGuitarUnknown;
                return;
            }

            var frets = _input.Settings.Settings.FiveFret;
            frets.RemoveAll(b => b.Device == row.Info.Id);
            frets.AddRange(bindings);
            _input.SaveBindings();
            Status = string.Format(CultureInfo.CurrentCulture, Strings.InputPresetAppliedFormat, Strings.InputPresetGuitar, row.Name);
        }

        /// <summary>Waits for the next control pressed, to bind to <paramref name="row"/>'s action; pressed again, stops waiting.</summary>
        [RelayCommand]
        private void Bind(InputActionRow row)
        {
            bool again = ReferenceEquals(_listening, row);
            StopListening();
            PendingMove = null;
            if (again)
            {
                Status = null;
                return;
            }

            _listening = row;
            row.IsListening = true;
            _detector = new BindingDetector(InputClock.Now, _input.Hub.Snapshot());
            Status = string.Format(CultureInfo.CurrentCulture, Strings.InputBindPromptFormat, row.Label);
            OnPropertyChanged(nameof(IsListening));
        }

        [RelayCommand]
        private void RemoveBinding(InputBindingChip chip)
        {
            _input.Settings.Settings.BindingsOf(chip.Profile).Remove(chip.Binding);
            _input.SaveBindings();
        }

        /// <summary>Puts a profile's bindings back to the keyboard's defaults.</summary>
        [RelayCommand]
        private void ResetProfile(InputProfileTab tab)
        {
            _input.Settings.Settings.SetBindings(tab.Kind, tab.Kind == InputProfileKind.Drums ? InputPresets.KeyboardDrums() : InputPresets.KeyboardFiveFret());
            _input.SaveBindings();
            Status = string.Format(CultureInfo.CurrentCulture, Strings.InputProfileResetFormat, tab.Header);
        }

        /// <summary>Looks for MIDI devices plugged in since, which winmm doesn't announce.</summary>
        [RelayCommand]
        private void Rescan() => _input.Hub.Rescan();

        [RelayCommand]
        private void ConfirmMove()
        {
            if (PendingMove is not { } move)
                return;
            var bindings = _input.Settings.Settings.BindingsOf(move.Profile);
            bindings.Remove(move.Other);
            bindings.Add(move.Binding);
            PendingMove = null;
            Status = null;
            _input.SaveBindings();
        }

        [RelayCommand]
        private void CancelMove()
        {
            PendingMove = null;
            Status = null;
        }

        private IReadOnlyList<InputActionRow> RowsOf(InputProfileKind profile) =>
            [.. InstrumentActions.Of(profile).Select(a => new InputActionRow(profile, a, ActionLabel(a)))];

        private void OnReceived(IReadOnlyList<RawInputEvent> inputs)
        {
            if (_detector is not { } detector || _listening is not { } row)
                return;
            foreach (var input in inputs)
            {
                if (detector.Feed(input) is { } found)
                {
                    StopListening();
                    Add(row, found);
                    return;
                }
            }
        }

        // Binds what Bind found to the row's action: as it is, or, when another action has it, once that's confirmed.
        private void Add(InputActionRow row, DetectedControl found)
        {
            var device = _input.Hub.Devices.FirstOrDefault(d => d.Id == found.Device) ?? KeyboardInputSource.Device;
            string? key = found.Control.Kind == InputControlKind.Key ? KeyboardInputSource.NameOf(found.Control.Index) : null;
            var binding = InputBinding.Of(row.Action, device, found.Control, key, found.Rest);
            var bindings = _input.Settings.Settings.BindingsOf(row.Profile);
            string control = ChipLabelOf(binding);
            if (bindings.FirstOrDefault(b => b.IsSameControl(binding)) is { } other)
            {
                if (other.Action == row.Action)
                {
                    Status = string.Format(CultureInfo.CurrentCulture, Strings.InputAlreadyBoundFormat, control, row.Label);
                    return;
                }

                PendingMove = new PendingInputMove(row.Profile, binding, other);
                Status = string.Format(CultureInfo.CurrentCulture, Strings.InputMoveFormat, control, ActionLabel(other.Action), row.Label);
                return;
            }

            bindings.Add(binding);
            Status = string.Format(CultureInfo.CurrentCulture, Strings.InputBoundFormat, control, row.Label);
            _input.SaveBindings();
        }

        private void StopListening()
        {
            if (_listening is { } row)
                row.IsListening = false;
            _listening = null;
            _detector = null;
            OnPropertyChanged(nameof(IsListening));
        }

        private void OnDevicesChanged(object? sender, EventArgs e)
        {
            RefreshDevices();
            RefreshChips();
        }

        private void OnBindingsChanged(object? sender, EventArgs e) => RefreshChips();

        private void RefreshDevices()
        {
            var settings = _input.Settings.Settings;
            var devices = _input.Hub.Devices;
            if (Devices.Select(d => d.Info).SequenceEqual(devices))
                return;
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(new InputDeviceRow(this, device, settings.TuningOf(device.Id)));
        }

        private void RefreshChips()
        {
            var connected = _input.Hub.Devices.Select(d => d.Id).ToHashSet();
            foreach (var tab in Profiles)
            {
                var bindings = _input.Settings.Settings.BindingsOf(tab.Kind);
                foreach (var row in tab.Actions)
                {
                    row.Chips = [.. bindings.Where(b => b.Action == row.Action).Select(b => new InputBindingChip(
                        tab.Kind, b, ChipLabelOf(b), b.DeviceName, b.DeviceKind == InputDeviceKind.Keyboard || connected.Contains(b.Device)))];
                }
            }
        }

        private void UpdateMeters()
        {
            double now = InputClock.Now;
            foreach (var row in Devices)
            {
                double activity = _input.Hub.LastActivity(row.Info.Id) is { } last ? Math.Clamp(1 - ((now - last) / MeterFall), 0, 1) : 0;
                if (Math.Abs(activity - row.Activity) > 0.01)
                    row.Activity = activity;
            }
        }

        /// <summary>A control as a chip shows it: on the keyboard, its key; on another device, the device's name and the control.</summary>
        private static string ChipLabelOf(InputBinding binding)
        {
            string control = binding.Control switch
            {
                InputControlKind.Key => KeyLabel(binding.Key ?? ""),
                InputControlKind.Button => string.Format(CultureInfo.CurrentCulture, Strings.InputControlButtonFormat, binding.Index + 1),
                InputControlKind.Axis => string.Format(CultureInfo.CurrentCulture, Strings.InputControlAxisFormat, binding.Index + 1, binding.Direction < 0 ? "−" : "+"),
                InputControlKind.Hat => string.Format(CultureInfo.CurrentCulture, Strings.InputControlHatFormat, binding.Index + 1, HatLabel(binding.Direction)),
                InputControlKind.MidiNote => string.Format(CultureInfo.CurrentCulture, Strings.InputControlNoteFormat, binding.Index),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.InputControlCcFormat, binding.Index),
            };
            return binding.DeviceKind == InputDeviceKind.Keyboard ? control : string.Format(CultureInfo.CurrentCulture, Strings.InputChipFormat, binding.DeviceName, control);
        }

        // A key's name as it's read: the digits and letters bare.
        private static string KeyLabel(string name) => name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6 ? name[5..] : name;

        private static string HatLabel(int direction) => direction switch
        {
            HatDirections.Up => Strings.InputHatUp,
            HatDirections.Right => Strings.InputHatRight,
            HatDirections.Down => Strings.InputHatDown,
            _ => Strings.InputHatLeft,
        };

        /// <summary>An action, in words.</summary>
        internal static string ActionLabel(InstrumentAction action) => action switch
        {
            InstrumentAction.Green => Strings.InputActionGreen,
            InstrumentAction.Red => Strings.InputActionRed,
            InstrumentAction.Yellow => Strings.InputActionYellow,
            InstrumentAction.Blue => Strings.InputActionBlue,
            InstrumentAction.Orange => Strings.InputActionOrange,
            InstrumentAction.StrumUp => Strings.InputActionStrumUp,
            InstrumentAction.StrumDown => Strings.InputActionStrumDown,
            InstrumentAction.Whammy => Strings.InputActionWhammy,
            InstrumentAction.StarPower => Strings.InputActionStarPower,
            InstrumentAction.Kick => Strings.InputActionKick,
            InstrumentAction.RedPad => Strings.InputActionRedPad,
            InstrumentAction.YellowTom => Strings.InputActionYellowTom,
            InstrumentAction.YellowCymbal => Strings.InputActionYellowCymbal,
            InstrumentAction.BlueTom => Strings.InputActionBlueTom,
            InstrumentAction.BlueCymbal => Strings.InputActionBlueCymbal,
            InstrumentAction.GreenTom => Strings.InputActionGreenTom,
            InstrumentAction.GreenCymbal => Strings.InputActionGreenCymbal,
            _ => Strings.InputActionOrangePad,
        };
    }
}
