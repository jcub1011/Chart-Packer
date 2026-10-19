using System;
using System.Collections.Generic;
using System.Globalization;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Calibration;
using ChartShared.Core.Charts;
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
    /// Nothing is stored until <see cref="SaveCommand"/>; closing the tool any other way leaves the stored latency as
    /// it was (AC C11.5).
    /// </para>
    /// </remarks>
    public sealed partial class CalibrationViewModel : ViewModelBase, IDisposable
    {
        private const int SampleRate = 48000;

        // The silent mix plays this long, far longer than anyone calibrates for.
        private static readonly TimeSpan MixLength = TimeSpan.FromHours(12);

        // How long after starting the first note is struck: a bar, so the first ones are seen coming.
        private static readonly TimeSpan Lead = TimeSpan.FromSeconds(4 * CalibrationChart.BeatSeconds);

        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly AudioLatencyService _latency;
        private readonly TimeProvider _clock;
        private readonly LatencyCalibration _calibration;

        private StemPlayer? _player;
        private IDisposable? _following;
        private long _startedAt;
        private bool _disposed;

        /// <param name="openOutput">Opens the audio output at a rate.</param>
        /// <param name="latency">The app's latency, which is loaded again when the tool saves.</param>
        /// <param name="clock">The monotonic clock the notes run on without audio; the system's if not given.</param>
        public CalibrationViewModel(Func<int, IAudioOutput> openOutput, AudioLatencyService latency, TimeProvider? clock = null)
        {
            _openOutput = openOutput;
            _latency = latency;
            _clock = clock ?? TimeProvider.System;
            var device = latency.Device ?? AudioDevice.Unknown;
            _calibration = new LatencyCalibration(latency.File, device.Name, device.Id);
            Value = _calibration.Value;
            DeviceName = DeviceNameOf(device.Name);

            var tempo = CalibrationChart.Tempo;
            var notes = CalibrationChart.Notes(CalibrationChart.BeatsIn(MixLength - Lead));
            Scene = new HighwayScene(
                [new HighwayLane(ChartInstrumentKind.FiveFret, HighwayLane.LaneCountOf(ChartInstrumentKind.FiveFret, false), "", notes, tempo, Lead)],
                tempo,
                Lead);
        }

        /// <summary>Raised when the tool has saved, and its window should close.</summary>
        public event EventHandler? CloseRequested;

        public static int MinLatency => -AudioLatencyFile.MaxMilliseconds;

        public static int MaxLatency => AudioLatencyFile.MaxMilliseconds;

        public static IReadOnlyList<LatencyStep> Steps { get; } =
            [new(-10), new(-5), new(-1), new(1), new(5), new(10)];

        /// <summary>The highway's open notes, one on every beat.</summary>
        public HighwayScene Scene { get; }

        /// <summary>The latency being tried, in milliseconds: positive when the audio is heard after the display shows it.</summary>
        [ObservableProperty]
        public partial int Value { get; set; }

        /// <summary>The name of the device being calibrated.</summary>
        [ObservableProperty]
        public partial string DeviceName { get; private set; }

        /// <summary>
        /// Whether the highway is drawn with no latency for now, while the hold button or Space is held, to show how far
        /// the latency moves the notes. <see cref="Value"/> is kept, and is what's saved.
        /// </summary>
        [ObservableProperty]
        public partial bool IsCalibrationOff { get; set; }

        /// <summary>Whether <see cref="Value"/> differs from the latency stored for the device, which Revert goes back to.</summary>
        public bool IsChanged => Value != _calibration.Stored;

        /// <summary>Why the clicks can't be played, or the latency couldn't be saved; null when all's well.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; private set; }

        public bool HasMessage => Message is not null;

        /// <summary>
        /// Where the highway is drawn: where the clicks are heard, less the latency, as playback draws a song. Without
        /// audio, the time since <see cref="Start"/>.
        /// </summary>
        public TimeSpan Now()
        {
            var position = _player is { IsPlaying: true } player ? player.SmoothPosition : _clock.GetElapsedTime(_startedAt);
            return LatencyCompensation.Drawn(position, IsCalibrationOff ? 0 : Value, 1);
        }

        /// <summary>Opens the output and starts the clicks. Never throws: a device that can't be opened is said in <see cref="Message"/>.</summary>
        public void Start()
        {
            if (_disposed || _player is not null)
                return;

            _startedAt = _clock.GetTimestamp();
            try
            {
                var output = _openOutput(SampleRate);
                var mixer = new StemMixer([], SampleRate);
                _player = new StemPlayer(mixer, output, _clock) { MinimumLength = MixLength };
                _player.Sounds = new MetronomeSchedule(CalibrationChart.Tempo, Lead, MixLength, SampleRate);
                _latency.Changed += OnLatencyChanged;
                _following = _latency.Follow(output);
                _player.Play();
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

        /// <summary>Stores the latency for the device, and closes the tool.</summary>
        [RelayCommand]
        private void Save()
        {
            _calibration.Value = Value;
            if (!_calibration.Save())
            {
                Message = SharedStrings.CalibrationSaveFailed;
                return;
            }

            _latency.Reload();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _latency.Changed -= OnLatencyChanged;
            _following?.Dispose();
            _player?.Dispose();
            _player = null;
        }

        /// <summary>The output moved to another device: its stored latency is loaded in place of this one's (AC C11.3).</summary>
        private void OnLatencyChanged(object? sender, EventArgs e)
        {
            if (_latency.Device is not { } device || (device.Name == _calibration.DeviceName && device.Id == _calibration.DeviceId))
                return;

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
