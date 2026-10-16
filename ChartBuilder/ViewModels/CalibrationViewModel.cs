using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Calibration;
using ChartShared.Core.Settings;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>What the calibration tool is doing.</summary>
    public enum CalibrationMode
    {
        /// <summary>The manual test: a light, a note and a beep on every beat, the beep moved by the latency.</summary>
        Manual,

        /// <summary>The tap test's first phase: the user taps along with the light, with no sound.</summary>
        TapVisual,

        /// <summary>The tap test's second phase: the user taps along with the beeps, with nothing shown.</summary>
        TapAudio,
    }

    /// <summary>A button that moves the latency by <see cref="Amount"/> milliseconds.</summary>
    public sealed record LatencyStep(int Amount)
    {
        public string Label => Amount.ToString("+0;−0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The calibration tool (charter-spec C11): measures how much later the audio is heard than the display shows it,
    /// for the output device, so that what's drawn while charting can be drawn that much earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The beats are on the app's monotonic clock. The light and the note are drawn at each beat; each beep is laid
    /// over a silent mix (<see cref="CalibrationSchedule"/>) to leave SDL's queue at its beat less the latency, so
    /// changing the latency moves the beep, never the light. The schedule is handed over anew when the latency
    /// changes, and once a second against the device's clock drifting from the system's.
    /// </para>
    /// <para>
    /// Nothing is stored until <see cref="SaveCommand"/>; closing the tool any other way leaves the stored latency as
    /// it was (AC C11.5).
    /// </para>
    /// </remarks>
    public sealed partial class CalibrationViewModel : ViewModelBase, IDisposable
    {
        /// <summary>How many beats each phase of the tap test has.</summary>
        public const int TapBeats = 16;

        private const int SampleRate = 48000;

        // The silent mix plays this long, far longer than anyone calibrates for.
        private static readonly TimeSpan MixLength = TimeSpan.FromHours(12);

        // How long after starting, or after a tap phase begins, the first beat is.
        private const double Lead = 1.5;

        // How long after a tap phase's last beat it ends, for a late last tap.
        private const double TapTail = 0.8;

        private static readonly TimeSpan AnchorInterval = TimeSpan.FromSeconds(1);

        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly AudioLatencyService _latency;
        private readonly TimeProvider _clock;
        private readonly LatencyCalibration _calibration;
        private readonly DispatcherTimer _timer;
        private readonly SynthSound _beep = Synth.CalibrationBeep(marked: false, SampleRate);
        private readonly SynthSound _markedBeep = Synth.CalibrationBeep(marked: true, SampleRate);
        private readonly List<double> _taps = [];

        private StemPlayer? _player;
        private IDisposable? _following;
        private CalibrationBeepsGiven _given = new();
        private TapPhase? _visualPhase;
        private bool _disposed;

        /// <param name="openOutput">Opens the audio output at a rate.</param>
        /// <param name="latency">The app's latency, which is loaded again when the tool saves.</param>
        /// <param name="clock">The monotonic clock the beats are on; the system's if not given.</param>
        public CalibrationViewModel(Func<int, IAudioOutput> openOutput, AudioLatencyService latency, TimeProvider? clock = null)
        {
            _openOutput = openOutput;
            _latency = latency;
            _clock = clock ?? TimeProvider.System;
            var device = latency.Device ?? AudioDevice.Unknown;
            _calibration = new LatencyCalibration(latency.File, device.Name, device.Id);
            Value = _calibration.Value;
            DeviceText = DeviceTextOf(device.Name);
            Beats = new CalibrationBeats(Now() + Lead, CalibrationBeats.ManualPeriod);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AnchorInterval };
            _timer.Tick += (_, _) => OnTick();
        }

        /// <summary>Raised when the tool has saved, and its window should close.</summary>
        public event EventHandler? CloseRequested;

        public static int MinLatency => -AudioLatencyFile.MaxMilliseconds;

        public static int MaxLatency => AudioLatencyFile.MaxMilliseconds;

        public static IReadOnlyList<LatencyStep> Steps { get; } =
            [new(-10), new(-5), new(-1), new(1), new(5), new(10)];

        /// <summary>The latency being tried, in milliseconds: positive when the audio is heard after the display shows it.</summary>
        [ObservableProperty]
        public partial int Value { get; set; }

        /// <summary>"Calibrating: …", naming the device.</summary>
        [ObservableProperty]
        public partial string DeviceText { get; private set; }

        /// <summary>Whether the manual test flashes the light.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLightShown))]
        public partial bool ShowLight { get; set; } = true;

        /// <summary>Whether the manual test scrolls a note down the highway to each beat.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsHighwayShown))]
        public partial bool ShowHighway { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLightShown), nameof(IsHighwayShown), nameof(IsTapTesting), nameof(IsManual))]
        public partial CalibrationMode Mode { get; private set; }

        /// <summary>The beats being played, on the monotonic clock: what the light and the note are drawn from.</summary>
        [ObservableProperty]
        public partial CalibrationBeats Beats { get; private set; }

        /// <summary>Why the beeps can't be played, or the latency couldn't be saved; null when all's well.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; private set; }

        /// <summary>What the tap test wants the user to do now.</summary>
        [ObservableProperty]
        public partial string? TapStatus { get; private set; }

        /// <summary>What the last tap test measured, or why it couldn't.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTapResult))]
        public partial string? TapResult { get; private set; }

        /// <summary>Whether the last tap test's taps were spread so widely that it's worth trying again.</summary>
        [ObservableProperty]
        public partial bool IsTapResultWide { get; private set; }

        /// <summary>The latency the last tap test measured, for <see cref="UseTapEstimateCommand"/>.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UseTapEstimateCommand))]
        public partial int? TapEstimate { get; private set; }

        public bool HasMessage => Message is not null;

        public bool HasTapResult => TapResult is not null;

        public bool IsManual => Mode == CalibrationMode.Manual;

        public bool IsTapTesting => Mode != CalibrationMode.Manual;

        /// <summary>Whether the light is shown: always in the tap test's first phase, never in its second.</summary>
        public bool IsLightShown => Mode switch
        {
            CalibrationMode.TapVisual => true,
            CalibrationMode.TapAudio => false,
            _ => ShowLight,
        };

        /// <summary>Whether the highway is shown: in the manual test only.</summary>
        public bool IsHighwayShown => Mode == CalibrationMode.Manual && ShowHighway;

        /// <summary>Now, in seconds on the monotonic clock the beats are on.</summary>
        public double Now() => (double)_clock.GetTimestamp() / _clock.TimestampFrequency;

        /// <summary>Opens the output and starts the manual test. Never throws: a device that can't be opened is said in <see cref="Message"/>.</summary>
        public void Start()
        {
            if (_disposed || _player is not null)
                return;

            try
            {
                var output = _openOutput(SampleRate);
                var mixer = new StemMixer([], SampleRate);
                _player = new StemPlayer(mixer, output, _clock) { MinimumLength = MixLength };
                _latency.Changed += OnLatencyChanged;
                _following = _latency.Follow(output);
                _player.Play();
            }
            catch (AudioDeviceException ex)
            {
                Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioUnavailableFormat, ex.Message);
            }

            StartManual();
            _timer.Start();
        }

        /// <summary>A tap on the light, or of Space, at <paramref name="time"/> on the monotonic clock (<see cref="Now"/>).</summary>
        public void Tap(double time)
        {
            if (Mode == CalibrationMode.Manual || _taps.Count >= TapBeats)
                return;

            _taps.Add(time);
            ShowTapStatus();
        }

        [RelayCommand]
        private void Nudge(int amount) => Value += amount;

        [RelayCommand]
        private void Reset() => Value = 0;

        /// <summary>Stores the latency for the device, and closes the tool.</summary>
        [RelayCommand]
        private void Save()
        {
            _calibration.Value = Value;
            if (!_calibration.Save())
            {
                Message = Strings.CalibrationSaveFailed;
                return;
            }

            _latency.Reload();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void StartTapTest()
        {
            TapResult = null;
            TapEstimate = null;
            IsTapResultWide = false;
            _visualPhase = null;
            StartTapPhase(CalibrationMode.TapVisual);
        }

        [RelayCommand]
        private void StopTapTest() => StartManual();

        [RelayCommand(CanExecute = nameof(HasTapEstimate))]
        private void UseTapEstimate()
        {
            if (TapEstimate is { } estimate)
                Value = estimate;
        }

        private bool HasTapEstimate() => TapEstimate is not null;

        partial void OnValueChanged(int value)
        {
            int clamped = Math.Clamp(value, MinLatency, MaxLatency);
            if (clamped != value)
            {
                Value = clamped;
                return;
            }

            // Heard from the next beat that hasn't been queued.
            if (Mode == CalibrationMode.Manual)
                Anchor();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _latency.Changed -= OnLatencyChanged;
            _following?.Dispose();
            _player?.Dispose();
            _player = null;
        }

        private void StartManual()
        {
            Mode = CalibrationMode.Manual;
            TapStatus = null;
            _given = new CalibrationBeepsGiven();
            Beats = new CalibrationBeats(Now() + Lead, CalibrationBeats.ManualPeriod);
            Anchor();
        }

        private void StartTapPhase(CalibrationMode phase)
        {
            Mode = phase;
            _taps.Clear();
            _given = new CalibrationBeepsGiven();
            Beats = new CalibrationBeats(Now() + Lead, CalibrationBeats.TapPeriod, BeatsPerBar: 0, Count: TapBeats);
            ShowTapStatus();
            Anchor();
        }

        private void OnTick()
        {
            if (Mode != CalibrationMode.Manual && Now() > Beats.TimeOf(TapBeats - 1) + TapTail)
                EndTapPhase();
            else
                Anchor();
        }

        private void EndTapPhase()
        {
            var phase = new TapPhase(Beats.FirstBeat, Beats.Period, [.. _taps]);
            if (Mode == CalibrationMode.TapVisual)
            {
                _visualPhase = phase;
                StartTapPhase(CalibrationMode.TapAudio);
                return;
            }

            if (_visualPhase is { } visual && TapTest.Estimate(visual, phase) is { } estimate)
            {
                TapEstimate = (int)Math.Round(Math.Clamp(estimate.LatencyMilliseconds, MinLatency, MaxLatency));
                IsTapResultWide = estimate.SpreadMilliseconds > TapTest.WideSpreadMilliseconds;
                TapResult = string.Format(
                    CultureInfo.CurrentCulture,
                    IsTapResultWide ? Strings.CalibrationTapResultWideFormat : Strings.CalibrationTapResultFormat,
                    TapEstimate,
                    Math.Round(estimate.SpreadMilliseconds),
                    estimate.VisualKept + estimate.AudioKept);
            }
            else
            {
                TapResult = Strings.CalibrationTapTooFew;
            }

            StartManual();
        }

        /// <summary>
        /// Hands the player a schedule of the beeps, tied to where it is now: none while only the light is shown, and in
        /// the tap test's second phase none moved by the latency, which is what that phase measures.
        /// </summary>
        private void Anchor()
        {
            if (_player is not { } player)
                return;

            if (Mode == CalibrationMode.TapVisual)
            {
                player.Sounds = null;
                return;
            }

            double latency = Mode == CalibrationMode.TapAudio ? 0 : Value / 1000.0;
            double now = Now();
            player.Sounds = new CalibrationSchedule(
                Beats, now, player.SmoothPosition.TotalSeconds, latency, _beep, _markedBeep, _given);
        }

        private void ShowTapStatus() => TapStatus = string.Format(
            CultureInfo.CurrentCulture,
            Mode == CalibrationMode.TapVisual ? Strings.CalibrationTapLightFormat : Strings.CalibrationTapBeepFormat,
            _taps.Count,
            TapBeats);

        /// <summary>The output moved to another device: its stored latency is loaded in place of this one's (AC C11.3).</summary>
        private void OnLatencyChanged(object? sender, EventArgs e)
        {
            if (_latency.Device is not { } device || (device.Name == _calibration.DeviceName && device.Id == _calibration.DeviceId))
                return;

            _calibration.OnDeviceChanged(device.Name, device.Id);
            DeviceText = DeviceTextOf(device.Name);
            Value = _calibration.Value;
        }

        private static string DeviceTextOf(string name) => string.Format(
            CultureInfo.CurrentCulture,
            Strings.CalibrationDeviceFormat,
            string.IsNullOrEmpty(name) ? Strings.CalibrationUnknownDevice : name);
    }
}
