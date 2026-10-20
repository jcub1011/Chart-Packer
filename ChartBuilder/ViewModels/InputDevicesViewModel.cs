using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ChartBuilder.Instruments;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Input;
using ChartShared.UI.Controls.Highway;
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

    /// <summary>A control bound to an action, as a chip beside it, lit while it's pressed.</summary>
    /// <param name="source">The connected device it's read from, as the router finds it; null when there's none.</param>
    /// <param name="control">The control read, without a direction, as the hub keeps it; a key's index is its <see cref="PhysicalKey"/>.</param>
    public sealed partial class InputBindingChip(InputActionRow row, InputBinding binding, string label, string toolTip, InputDeviceId? source, InputControl control)
        : ObservableObject
    {
        public InputProfileKind Profile => row.Profile;

        public InputBinding Binding { get; } = binding;

        public string Label { get; } = label;

        public string ToolTip { get; } = toolTip;

        /// <summary>Whether its device is connected now, as the router finds it: by its name, too, when it's come back with another key.</summary>
        public bool IsConnected => Source is not null;

        public InputDeviceId? Source { get; } = source;

        public InputControl Control { get; } = control;

        /// <summary>Its action's colour, as the highway draws it: the chip's outline while it's pressed.</summary>
        public IBrush LaneBrush => row.LaneBrush;

        /// <summary>Its action's colour, faint: the chip's fill while it's pressed.</summary>
        public IBrush LaneTintBrush => row.LaneTintBrush;

        [ObservableProperty]
        public partial bool IsPressed { get; set; }

        /// <summary>How far it's pressed, from 0 to 1: an axis or MIDI control by how far it's moved, a pad by how hard it was hit, anything else all or nothing.</summary>
        internal double Level { get; set; }
    }

    /// <summary>An action of a profile, with the controls bound to it, lit in its colour while they're pressed.</summary>
    public sealed partial class InputActionRow(InputProfileKind profile, InstrumentAction action, string label) : ObservableObject
    {
        public InputProfileKind Profile { get; } = profile;

        public InstrumentAction Action { get; } = action;

        public string Label { get; } = label;

        /// <summary>The action's colour, as the highway draws its lane: the stripe at the row's start.</summary>
        public IBrush LaneBrush { get; } = new ImmutableSolidColorBrush(LaneColorOf(action));

        /// <summary>The action's colour, faint: the row's background as its controls are pressed.</summary>
        public IBrush LaneTintBrush { get; } = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(LaneColorOf(action), 0x48));

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasChips))]
        public partial IReadOnlyList<InputBindingChip> Chips { get; set; } = [];

        public bool HasChips => Chips.Count > 0;

        /// <summary>Whether Bind is waiting for this action's control.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BindText))]
        public partial bool IsListening { get; set; }

        public string BindText => IsListening ? Strings.InputBindListening : Strings.InputBindButton;

        /// <summary>Whether any of its controls is pressed.</summary>
        [ObservableProperty]
        public partial bool IsPressed { get; set; }

        /// <summary>How far its controls are pressed, the furthest of them, from 0 to 1: how much of the row is lit.</summary>
        [ObservableProperty]
        public partial double Level { get; set; }

        // The highway's colour for an action's lane; strum, which has no lane, a neutral grey.
        private static Color LaneColorOf(InstrumentAction action) => action switch
        {
            InstrumentAction.Green or InstrumentAction.GreenTom or InstrumentAction.GreenCymbal => HighwayPalette.Green,
            InstrumentAction.Red or InstrumentAction.RedPad => HighwayPalette.Red,
            InstrumentAction.Yellow or InstrumentAction.YellowTom or InstrumentAction.YellowCymbal => HighwayPalette.Yellow,
            InstrumentAction.Blue or InstrumentAction.BlueTom or InstrumentAction.BlueCymbal => HighwayPalette.Blue,
            InstrumentAction.Orange or InstrumentAction.OrangePad => HighwayPalette.Orange,
            InstrumentAction.Kick => HighwayPalette.Kick,
            InstrumentAction.Whammy => HighwayPalette.Open,
            InstrumentAction.StarPower => HighwayPalette.StarPower,
            _ => HighwayPalette.BlackFretEdge,
        };
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
        // How long a device's light takes to fade out, or a pad's row to fall to nothing, after it last did something, in seconds.
        private const double MeterFall = 0.4;

        // How long a pad's chip stays lit after it's hit, in seconds: kits send their note-offs late, or not at all.
        private const double StrikeLit = 0.12;

        private readonly InputService _input;
        private readonly Func<InputDeviceInfo, Task<bool>>? _calibrate;
        private readonly IDisposable _lease;
        private readonly DispatcherTimer _meters;
        private readonly Dictionary<(InputDeviceId, int), (double Time, int Velocity)> _strikes = [];

        // Each chip's device's MIDI floor, its MinVelocity, as the chips were last made.
        private readonly Dictionary<InputDeviceId, int> _floors = [];

        // The keys bound in either profile, by PhysicalKey, as the chips were last made.
        private readonly HashSet<int> _boundKeys = [];
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
            SelectedProfile = Profiles[0];
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

        /// <summary>The device whose own settings are shown.</summary>
        [ObservableProperty]
        public partial InputDeviceRow? SelectedDevice { get; set; }

        /// <summary>The profile whose actions are shown.</summary>
        [ObservableProperty]
        public partial InputProfileTab? SelectedProfile { get; set; }

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
        /// A key pressed or let go in the window: it lights the chips it's bound to, and while Bind waits, it's the control to
        /// bind, and Esc stops waiting. Space, Esc, P and N are the editor's and can't be bound. Only a key to bind is played
        /// through the devices; the hub only sees the rest (<see cref="InputHub.Observe"/>), so a playtest running meanwhile
        /// isn't played by typing here.
        /// </summary>
        /// <param name="typing">Whether a text box has the keyboard, which then keeps the keys bound, to type them.</param>
        /// <returns>
        /// Whether the key is taken: while Bind waits, any; otherwise a bound key, so a fret or strum tried here doesn't also
        /// press the button focused (Enter) or move the focus (the arrows).
        /// </returns>
        public bool HandleKey(PhysicalKey key, bool down, bool typing = false)
        {
            if (_listening is null)
            {
                _input.ObserveKey(key, down);
                return !typing && _boundKeys.Contains((int)key);
            }

            if (down && key == PhysicalKey.Escape)
            {
                _input.ObserveKey(key, down);
                StopListening();
                Status = null;
                return true;
            }

            if (key is PhysicalKey.Space or PhysicalKey.Escape or PhysicalKey.P or PhysicalKey.N)
            {
                _input.ObserveKey(key, down);
                if (down)
                    Status = Strings.InputKeyReserved;
                return true;
            }

            _input.PostKey(key, down);
            return true;
        }

        /// <summary>Lets go of the keys held, as the window loses the keyboard, so their chips don't stay lit.</summary>
        public void ReleaseKeys() => _input.Hub.ReleaseKeys();

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

            StopBinding();
            Status = null;
            bool ran = await _calibrate(row.Info);
            if (_disposed)
                return;

            if (ran)
                row.ShowOffset(_input.Settings.Settings.TuningOf(row.Info.Id).OffsetMs);
            else
                Status = Strings.InputCalibrationAlreadyOpen;
        }

        /// <summary>Bind stops waiting, and a move it found is let go, as the calibration tool opens, so its taps aren't taken for a control to bind.</summary>
        public void StopBinding()
        {
            StopListening();
            PendingMove = null;
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
        private void ResetProfile(InputProfileTab? tab)
        {
            if (tab is null)
                return;
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
            foreach (var input in inputs)
            {
                if (input.Control.Kind == InputControlKind.MidiNote && input.Velocity > 0)
                    _strikes[(input.Device, input.Control.Index)] = (input.Time, input.Velocity);
            }

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
            var selected = SelectedDevice?.Info.Id;
            Devices.Clear();
            foreach (var device in devices)
                Devices.Add(new InputDeviceRow(this, device, settings.TuningOf(device.Id)));
            SelectedDevice = Devices.FirstOrDefault(d => d.Info.Id == selected) ?? Devices.FirstOrDefault();
        }

        // The chips made again, as the bindings or devices change. Those pressed stay pressed, so an axis held in its
        // hysteresis isn't let go by a device's setting saved.
        private void RefreshChips()
        {
            var settings = _input.Settings.Settings;
            var devices = _input.Hub.Devices;
            foreach (var tab in Profiles)
            {
                var bindings = settings.BindingsOf(tab.Kind);
                foreach (var row in tab.Actions)
                {
                    var held = row.Chips.Where(c => c.IsPressed).Select(c => c.Binding).ToHashSet();
                    row.Chips = [.. bindings.Where(b => b.Action == row.Action).Select(b => new InputBindingChip(
                        row, b, ChipLabelOf(b), b.DeviceName, b.IsComplete ? InputRouter.DeviceOf(b, devices) : null, ControlOf(b))
                    {
                        IsPressed = held.Contains(b),
                    })];
                }
            }

            _floors.Clear();
            _boundKeys.Clear();
            foreach (var chip in Profiles.SelectMany(t => t.Actions).SelectMany(r => r.Chips))
            {
                if (chip.Source is { Kind: InputDeviceKind.Midi } device && !_floors.ContainsKey(device))
                    _floors[device] = settings.TuningOf(device).Sanitized().MinVelocity;
                if (chip.Control.Kind == InputControlKind.Key)
                    _boundKeys.Add(chip.Control.Index);
            }
        }

        // The control a binding reads, as the hub keeps it: a key by its PhysicalKey, anything else by its index.
        private static InputControl ControlOf(InputBinding binding) => binding.Control == InputControlKind.Key
            ? new InputControl(InputControlKind.Key, KeyboardInputSource.IndexOf(binding.Key ?? "") ?? -1)
            : new InputControl(binding.Control, binding.Index);

        private void UpdateMeters()
        {
            double now = InputClock.Now;
            foreach (var row in Devices)
            {
                double activity = _input.Hub.LastActivity(row.Info.Id) is { } at ? Math.Clamp(1 - ((now - at) / MeterFall), 0, 1) : 0;
                if (Math.Abs(activity - row.Activity) > 0.01)
                    row.Activity = activity;
            }

            if (SelectedProfile is not { } profile)
                return;
            foreach (var row in profile.Actions)
            {
                bool pressed = false;
                double level = 0;
                foreach (var chip in row.Chips)
                {
                    Read(chip, now);
                    pressed |= chip.IsPressed;
                    level = Math.Max(level, chip.Level);
                }

                row.IsPressed = pressed;
                if (Math.Abs(level - row.Level) > 0.01 || (level == 0 && row.Level != 0))
                    row.Level = level;
            }
        }

        // Whether a chip's control is pressed now, and how far: a pad by its last hit (lit as it's hit, its row falling off
        // after), anything else, keys included, by its value as the hub last saw it.
        private void Read(InputBindingChip chip, double now)
        {
            var binding = chip.Binding;
            bool pressed = false;
            double level = 0;
            if (chip.Source is { } device)
            {
                switch (binding.Control)
                {
                    case InputControlKind.MidiNote when _strikes.TryGetValue((device, chip.Control.Index), out var strike):
                        // A hit softer than the device's floor isn't played, but still shows, to set the floor by.
                        double since = now - strike.Time;
                        pressed = since < StrikeLit && strike.Velocity >= _floors.GetValueOrDefault(device);
                        level = strike.Velocity / 127.0 * Math.Clamp(1 - (since / MeterFall), 0, 1);
                        break;
                    case InputControlKind.MidiNote:
                        break;
                    default:
                        if (_input.Hub.LastValue(device, chip.Control) is not { } value)
                            break;
                        pressed = ControlReading.IsPressed(binding.Control, binding.Direction, binding.Rest, binding.Threshold, value, chip.IsPressed);
                        level = binding.Control is InputControlKind.Axis or InputControlKind.MidiControl
                            ? ControlReading.Travel(binding.Control, binding.Direction, binding.Rest, value)
                            : pressed ? 1 : 0;
                        break;
                }
            }

            chip.IsPressed = pressed;
            chip.Level = level;
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
