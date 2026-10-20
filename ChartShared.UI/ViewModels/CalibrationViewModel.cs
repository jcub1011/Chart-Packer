using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Calibration;
using ChartShared.Core.Input;
using ChartShared.Core.Settings;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>A button that moves the latency by <see cref="Amount"/> milliseconds.</summary>
    public sealed record LatencyStep(int Amount)
    {
        public string Label => Amount.ToString("+0;−0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The calibration tool (charter-spec C11), shared by Chart Packer and Chart Builder: measures how much later the
    /// audio is heard than the display shows it, for the output device, so that what's drawn against the audio can be
    /// drawn that much earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It plays <see cref="CalibrationChart"/> as playback does: the metronome clicks on each beat of a silent mix,
    /// and the real highway draws an open note on each, struck at the line, at the player's position less the latency
    /// (<see cref="LatencyCompensation"/>). So changing the latency moves the notes, never the clicks, just as it moves
    /// what's drawn while a song plays.
    /// </para>
    /// <para>
    /// Or the latency can be measured by tapping along with clicks of the test's own, in place of the metronome's and
    /// counted in (<see cref="BeginTapsCommand"/>, <see cref="TapClicks"/>), with the notes hidden: each tap is placed on
    /// the clicks' timeline, less its device's own offset, so what's left is the latency
    /// (<see cref="AudioTapTest"/>). The result is put in <see cref="Value"/>, to be checked against the notes, not saved.
    /// The taps come from every input device the app has (<see cref="ICalibrationTaps"/>), or else from the keyboard and
    /// a click on the highway, with no offset.
    /// </para>
    /// <para>
    /// Keys and clicks are timed when they're handled (charter-spec C15 item 5): Avalonia's key events carry no timestamp,
    /// and a click's is the OS's message time, only as fine as its 15.6 ms tick on Windows. Through the app's devices, the
    /// keyboard's offset was measured the same way, so the time taken to handle a key comes off with it.
    /// </para>
    /// <para>
    /// Nothing is stored until <see cref="SaveCommand"/>; closing the tool any other way leaves the stored latency as
    /// it was (AC C11.5).
    /// </para>
    /// </remarks>
    public sealed partial class CalibrationViewModel : CalibrationViewModelBase
    {
        private const int SampleRate = 48000;

        // The silent mix plays this long, far longer than anyone calibrates for.
        private static readonly TimeSpan MixLength = TimeSpan.FromHours(12);

        // How long after starting the first note is struck: a bar, so the first ones are seen coming.
        private static readonly TimeSpan Lead = TimeSpan.FromSeconds(4 * CalibrationChart.BeatSeconds);

        // Often, so the count-in's numbers change on the beat.
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);

        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly AudioLatencyService _latency;
        private readonly TimeProvider _clock;
        private readonly LatencyCalibration _calibration;
        private readonly ICalibrationTaps? _tapInput;
        private readonly Func<double> _inputClock;
        private readonly DispatcherTimer _timer;

        // The tap test's clicks and taps, and when it started, in seconds of song.
        private readonly List<double> _taps = [];
        private double[] _clicks = [];
        private double _tapStart;

        private StemPlayer? _player;
        private MetronomeSchedule? _metronome;
        private IDisposable? _following;
        private IDisposable? _listening;
        private long _startedAt;
        private bool _disposed;

        /// <param name="openOutput">Opens the audio output at a rate.</param>
        /// <param name="latency">The app's latency, which is loaded again when the tool saves.</param>
        /// <param name="taps">The app's input devices, for the tap test; without, the keyboard and the mouse in the tool's window.</param>
        /// <param name="clock">The monotonic clock the notes run on without audio; the system's if not given.</param>
        /// <param name="inputClock">The input clock, in seconds, which the taps are timed on; <see cref="InputClock.Now"/> if not given.</param>
        public CalibrationViewModel(Func<int, IAudioOutput> openOutput, AudioLatencyService latency, ICalibrationTaps? taps = null, TimeProvider? clock = null, Func<double>? inputClock = null)
        {
            _openOutput = openOutput;
            _latency = latency;
            _tapInput = taps;
            _clock = clock ?? TimeProvider.System;
            _inputClock = inputClock ?? (() => InputClock.Now);
            var device = latency.Device ?? AudioDevice.Unknown;
            _calibration = new LatencyCalibration(latency.File, device.Name, device.Id);
            Value = _calibration.Value;
            DeviceName = DeviceNameOf(device.Name);
            Scene = SceneOf(CalibrationChart.Notes(CalibrationChart.BeatsIn(MixLength - Lead)), Lead);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
            _timer.Tick += (_, _) => Tick();
        }

        public static int MinLatency => -AudioLatencyFile.MaxMilliseconds;

        public static int MaxLatency => AudioLatencyFile.MaxMilliseconds;

        public static IReadOnlyList<LatencyStep> Steps { get; } =
            [new(-10), new(-5), new(-1), new(1), new(5), new(10)];

        /// <summary>The highway's open notes, one on every beat.</summary>
        public override HighwayScene Scene { get; }

        public override string Title => SharedStrings.CalibrationWindowTitle;

        /// <summary>The latency being tried, in milliseconds: positive when the audio is heard after the display shows it.</summary>
        [ObservableProperty]
        public partial int Value { get; set; }

        /// <summary>The name of the device being calibrated.</summary>
        [ObservableProperty]
        public partial string DeviceName { get; private set; }

        /// <summary>Whether the hold button is held down, which holds calibration off.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCalibrationOff))]
        public partial bool IsHeldByButton { get; set; }

        /// <summary>Whether Space is held down, which holds calibration off.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCalibrationOff))]
        public partial bool IsHeldBySpace { get; set; }

        /// <summary>
        /// Whether the highway is drawn with no latency for now, while the hold button or Space is held, to show how far
        /// the latency moves the notes. <see cref="Value"/> is kept, and is what's saved.
        /// </summary>
        public bool IsCalibrationOff => IsHeldByButton || IsHeldBySpace;

        /// <summary>Whether <see cref="Value"/> differs from the latency stored for the device, which Revert goes back to.</summary>
        public bool IsChanged => Value != _calibration.Stored;

        /// <summary>Where the tap test is.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTapping), nameof(TapStartLabel))]
        [NotifyCanExecuteChangedFor(nameof(BeginTapsCommand))]
        public partial DeviceTestState TapState { get; private set; }

        /// <summary>Whether the tap test is running, when every key but Escape is a tap.</summary>
        public bool IsTapping => TapState == DeviceTestState.Running;

        /// <summary>Start, or Try again once a tap test has run.</summary>
        public string TapStartLabel => TapState == DeviceTestState.Done ? SharedStrings.CalibrationTryAgain : SharedStrings.CalibrationStartTest;

        /// <summary>What the tap test wants now: the instructions, the click it's on, or what it measured.</summary>
        [ObservableProperty]
        public partial string TapStatus { get; private set; } = SharedStrings.CalibrationTapReady;

        /// <summary>Whether the last tap test's taps were spread so widely that it's worth trying again, too few, or out of range.</summary>
        [ObservableProperty]
        public partial bool IsTapStatusWarning { get; private set; }

        /// <summary>Whether the taps come from the app's input devices, each with its offset taken off.</summary>
        public bool HasDeviceOffsets => _tapInput is not null;

        /// <summary>
        /// Where the highway is drawn: where the clicks are heard, less the latency, as playback draws a song. Without
        /// audio, the time since <see cref="Start"/>.
        /// </summary>
        public override TimeSpan Now()
        {
            var position = _player is { IsPlaying: true } player ? player.SmoothPosition : _clock.GetElapsedTime(_startedAt);
            return LatencyCompensation.Drawn(position, IsCalibrationOff ? 0 : Value, 1);
        }

        /// <summary>Opens the output and starts the clicks. Never throws: a device that can't be opened is said in <see cref="Message"/>.</summary>
        public override void Start()
        {
            if (_disposed || _player is not null)
                return;

            _startedAt = _clock.GetTimestamp();
            try
            {
                var output = _openOutput(SampleRate);
                var mixer = new StemMixer([], SampleRate);
                _player = new StemPlayer(mixer, output, _clock) { MinimumLength = MixLength };
                _metronome = new MetronomeSchedule(CalibrationChart.Tempo, Lead, MixLength, SampleRate);
                _player.Sounds = _metronome;
                _player.Stopped += OnPlayerStopped;
                _latency.Changed += OnLatencyChanged;
                _following = _latency.Follow(output);
                _player.Play();
                BeginTapsCommand.NotifyCanExecuteChanged();
            }
            catch (AudioDeviceException ex)
            {
                Message = string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationAudioUnavailableFormat, ex.Message);
            }
        }

        [RelayCommand]
        private void Nudge(int amount) => Value += amount;

        [RelayCommand]
        private void Reset() => Value = 0;

        /// <summary>Goes back to the latency stored for the device, as when the tool opened or it was last saved.</summary>
        [RelayCommand(CanExecute = nameof(IsChanged))]
        private void Revert() => Value = _calibration.Stored;

        /// <summary>
        /// The tap test's clicks, for a test started at <paramref name="from"/>: only the clicks to tap
        /// (<see cref="AudioTapTest"/>), each bar's first accented, with none before them, through getting ready and the
        /// count-in, and none after.
        /// </summary>
        public static MetronomeSchedule TapClicks(TimeSpan from, int sampleRate)
        {
            var clicks = AudioTapTest.ClickTimes(from);
            return new MetronomeSchedule(
                CalibrationChart.Tempo,
                TimeSpan.FromSeconds(clicks[0]),
                TimeSpan.FromSeconds(clicks[^1] + (CalibrationChart.BeatSeconds / 2)),
                sampleRate);
        }

        /// <summary>
        /// Starts the tap test, or starts it again: the notes hide, and the metronome goes quiet for a bar to get ready and a
        /// bar counted in on screen, then the test's own clicks are tapped.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanBeginTaps))]
        private void BeginTaps()
        {
            if (_player is not { } player)
                return;

            _taps.Clear();
            var from = player.Position;
            _tapStart = from.TotalSeconds;
            _clicks = AudioTapTest.ClickTimes(from);
            player.Sounds = TapClicks(from, SampleRate);
            IsTapStatusWarning = false;
            HidesNotes = true;
            TapState = DeviceTestState.Running;
            _listening = _tapInput?.Listen(OnTapped);
            Tick();
            _timer.Start();
        }

        private bool CanBeginTaps() => _player is { IsPlaying: true } && TapState != DeviceTestState.Running;

        /// <summary>
        /// A key went down in the window while the tap test runs, the window handing on only its first press, or a key it
        /// handed on went up, which may be after the test: handed on to the app's keyboard device when there is one, so it's
        /// never left held there, or else a key down is timed now.
        /// </summary>
        public void Key(PhysicalKey key, bool down)
        {
            if (down && !IsTapping)
                return;

            if (_tapInput is { } input)
                input.Key(key, down);
            else if (down)
                OnTapped(_inputClock(), 0);
        }

        /// <summary>A click or touch on the highway while the tap test runs: a tap now, with no offset.</summary>
        public void TapNow()
        {
            if (IsTapping)
                OnTapped(_inputClock(), 0);
        }

        /// <summary>
        /// A tap at <paramref name="time"/> on the input clock, from a device whose own offset is <paramref name="offsetMs"/>:
        /// placed on the clicks' timeline, as the player was then, less the offset.
        /// </summary>
        private void OnTapped(double time, int offsetMs)
        {
            if (!IsTapping || _player is not { } player)
                return;

            double ago = Math.Max(0, _inputClock() - time);
            _taps.Add(player.SmoothPosition.TotalSeconds - ago - (offsetMs / 1000.0));
        }

        /// <summary>
        /// The bar to get ready, the count-in, the click the test is on, and its end once the last click's tap has had time
        /// to come.
        /// </summary>
        private void Tick()
        {
            if (_player is not { } player || _clicks.Length == 0)
                return;

            double at = player.Position.TotalSeconds;
            if (at >= AudioTapTest.EndSeconds(_clicks))
            {
                FinishTaps();
                return;
            }

            // Counted on the clicks' beat with no latency, so the latency being measured never moves the count, and the
            // first click comes on the beat after 1; each click from half a beat before it.
            var step = CalibrationCountIn.At(
                (at - _tapStart) / CalibrationChart.BeatSeconds,
                AudioTapTest.ReadyBeats,
                AudioTapTest.ReadyBeats + AudioTapTest.CountInBeats,
                AudioTapTest.ClickCount);
            switch (step.Phase)
            {
                case CountInPhase.GetReady:
                    CountIn = SharedStrings.CalibrationGetReady;
                    TapStatus = SharedStrings.CalibrationGetReady;
                    break;
                case CountInPhase.CountIn:
                    CountIn = step.Number.ToString(CultureInfo.CurrentCulture);
                    TapStatus = SharedStrings.CalibrationGetReady;
                    break;
                default:
                    CountIn = SharedStrings.CalibrationTapNow;
                    TapStatus = string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationTapProgressFormat, step.Number, AudioTapTest.ClickCount);
                    break;
            }
        }

        private void FinishTaps()
        {
            StopTaps();
            TapState = DeviceTestState.Done;
            if (AudioTapTest.Estimate(_clicks, _taps) is not { } estimate)
            {
                IsTapStatusWarning = true;
                TapStatus = SharedStrings.CalibrationTapTooFew;
                return;
            }

            int measured = (int)Math.Round(estimate.OffsetMilliseconds);
            int latency = Math.Clamp(measured, MinLatency, MaxLatency);
            bool wide = estimate.SpreadMilliseconds > TapTest.WideSpreadMilliseconds;
            IsTapStatusWarning = wide || latency != measured;
            TapStatus = string.Format(
                CultureInfo.CurrentCulture,
                wide ? SharedStrings.CalibrationTapResultWideFormat : SharedStrings.CalibrationTapResultFormat,
                measured,
                Math.Round(estimate.SpreadMilliseconds),
                estimate.Kept);
            if (latency != measured)
                TapStatus += " " + string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationTapClampedFormat, latency);
            Value = latency;
        }

        /// <summary>Stops a tap test that's running, as the output moves to another device: what it heard is for the old one.</summary>
        private void CancelTaps()
        {
            if (!IsTapping)
                return;

            StopTaps();
            TapState = DeviceTestState.Ready;
            IsTapStatusWarning = false;
            TapStatus = SharedStrings.CalibrationTapReady;
        }

        // Stops listening and ticking, and shows the notes again, clicked by the metronome again.
        private void StopTaps()
        {
            _timer.Stop();
            if (_player is { } player)
                player.Sounds = _metronome;
            _listening?.Dispose();
            _listening = null;
            CountIn = null;
            HidesNotes = false;
        }

        /// <summary>Stores the latency for the device.</summary>
        protected override bool OnSave()
        {
            _calibration.Value = Value;
            if (!_calibration.Save())
            {
                Message = SharedStrings.CalibrationSaveFailed;
                return false;
            }

            _latency.Reload();
            return true;
        }

        partial void OnIsHeldByButtonChanged(bool value) => ShowHeld();

        partial void OnIsHeldBySpaceChanged(bool value) => ShowHeld();

        private void ShowHeld() => Banner = IsCalibrationOff ? SharedStrings.CalibrationOffBadge : null;

        partial void OnValueChanged(int value)
        {
            int clamped = Math.Clamp(value, MinLatency, MaxLatency);
            if (clamped != value)
            {
                Value = clamped;
                return;
            }

            OnChangedChanged();
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _listening?.Dispose();
            _listening = null;
            _latency.Changed -= OnLatencyChanged;
            _following?.Dispose();
            if (_player is { } player)
            {
                player.Stopped -= OnPlayerStopped;
                player.Dispose();
                _player = null;
            }
        }

        // On the playback thread.
        private void OnPlayerStopped(object? sender, EventArgs e) => Dispatcher.UIThread.Post(OnPlaybackStopped);

        /// <summary>
        /// The clicks stopped by themselves, as the output failed: a tap test running can't finish, as the player's position
        /// no longer moves, so it's stopped, and the test can't be started again.
        /// </summary>
        private void OnPlaybackStopped()
        {
            if (_disposed || _player is not { } player)
                return;

            CancelTaps();
            if (player.Error is { } error)
                Message = string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationAudioUnavailableFormat, error);
            BeginTapsCommand.NotifyCanExecuteChanged();
        }

        /// <summary>The output moved to another device: its stored latency is loaded in place of this one's (AC C11.3).</summary>
        private void OnLatencyChanged(object? sender, EventArgs e)
        {
            if (_latency.Device is not { } device || (device.Name == _calibration.DeviceName && device.Id == _calibration.DeviceId))
                return;

            CancelTaps();
            _calibration.OnDeviceChanged(device.Name, device.Id);
            DeviceName = DeviceNameOf(device.Name);
            Value = _calibration.Value;
            OnChangedChanged();
        }

        // Whether the value differs from the stored one may have changed: the value moved, or another device's was loaded.
        private void OnChangedChanged()
        {
            OnPropertyChanged(nameof(IsChanged));
            RevertCommand.NotifyCanExecuteChanged();
        }

        private static string DeviceNameOf(string name) => string.IsNullOrEmpty(name) ? SharedStrings.CalibrationUnknownDevice : name;
    }
}
