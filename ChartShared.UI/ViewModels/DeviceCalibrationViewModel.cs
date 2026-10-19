using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using ChartShared.Core.Calibration;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>Where the input-device test is.</summary>
    public enum DeviceTestState
    {
        /// <summary>Waiting for Start.</summary>
        Ready,

        /// <summary>The count-in, then the notes.</summary>
        Running,

        /// <summary>The notes are over, and what they measured is shown.</summary>
        Done,
    }

    /// <summary>
    /// The calibration tool's input-device test (charter-spec C11 item 2): measures how much later than the screen shows a
    /// note a device reports it hit, so the playtest can take that off its inputs (<see cref="DeviceTuning.OffsetMs"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It's seen, not heard: after Start, two bars lead in, then <see cref="CalibrationChart.DeviceNoteCount"/> open notes
    /// fall, one a beat, with no sound. The highway is drawn at the time since Start on the input clock, which the presses
    /// are timed on too, so a press is measured against the note as it was drawn. That's the timeline the playtest judges
    /// presses on, so the audio/visual latency drops out, and what's left is the device's lag, the display's, and the
    /// player's habit (<see cref="TapTest"/>).
    /// </para>
    /// <para>Nothing is stored until <see cref="CalibrationViewModelBase.SaveCommand"/>.</para>
    /// </remarks>
    public sealed partial class DeviceCalibrationViewModel : CalibrationViewModelBase
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(30);

        private readonly ICalibrationInput _input;
        private readonly Func<double> _clock;
        private readonly double[] _notes = CalibrationChart.DeviceNoteTimes();
        private readonly List<double> _taps = [];
        private readonly DispatcherTimer _timer;

        private IDisposable? _listening;
        private double _startedAt;
        private bool _disposed;

        /// <param name="input">The device to measure.</param>
        /// <param name="clock">The input clock, in seconds, which the presses are timed on; <see cref="InputClock.Now"/> if not given.</param>
        public DeviceCalibrationViewModel(ICalibrationInput input, Func<double>? clock = null)
        {
            ArgumentNullException.ThrowIfNull(input);
            _input = input;
            _clock = clock ?? (() => InputClock.Now);
            DeviceName = input.DeviceName;
            SavedOffset = input.OffsetMs;

            var tempo = CalibrationChart.Tempo;
            var notes = CalibrationChart.Notes(CalibrationChart.DeviceNoteCount, CalibrationChart.DeviceLeadBeats);
            Scene = new HighwayScene(
                [new HighwayLane(ChartInstrumentKind.FiveFret, HighwayLane.LaneCountOf(ChartInstrumentKind.FiveFret, false), "", notes, tempo, TimeSpan.Zero)],
                tempo,
                TimeSpan.Zero);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
            _timer.Tick += (_, _) => Tick();
        }

        public override HighwayScene Scene { get; }

        public override string Title => SharedStrings.CalibrationDeviceWindowTitle;

        /// <summary>The name of the device being calibrated.</summary>
        public string DeviceName { get; }

        /// <summary>Whether the device is the keyboard, whose keys the window hands on.</summary>
        public bool IsKeyboard => _input.IsKeyboard;

        /// <summary>The offset stored for the device, in milliseconds.</summary>
        public int SavedOffset { get; }

        /// <summary>"Saved offset: …".</summary>
        public string SavedOffsetText => string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationSavedOffsetFormat, SavedOffset);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRunning), nameof(StartLabel))]
        [NotifyCanExecuteChangedFor(nameof(BeginCommand))]
        public partial DeviceTestState State { get; private set; }

        public bool IsRunning => State == DeviceTestState.Running;

        /// <summary>Start, or Try again once a test has run.</summary>
        public string StartLabel => State == DeviceTestState.Done ? SharedStrings.CalibrationTryAgain : SharedStrings.CalibrationStartTest;

        /// <summary>What the test wants now: the instructions, the note it's on, or what it measured.</summary>
        [ObservableProperty]
        public partial string Status { get; private set; } = SharedStrings.CalibrationDeviceReady;

        /// <summary>Whether the last test's taps were spread so widely that it's worth trying again, or too few to measure.</summary>
        [ObservableProperty]
        public partial bool IsStatusWarning { get; private set; }

        /// <summary>The offset the last test measured, within ±<see cref="DeviceTuning.MaxOffsetMs"/>, which Save stores; null before one has.</summary>
        [ObservableProperty]
        public partial int? Measured { get; private set; }

        public override TimeSpan Now() => State == DeviceTestState.Running ? TimeSpan.FromSeconds(_clock() - _startedAt) : TimeSpan.Zero;

        /// <summary>Starts listening to the device.</summary>
        public override void Start()
        {
            if (_disposed || _listening is not null)
                return;

            _listening = _input.Listen(OnPressed);
        }

        /// <summary>A key went down or up in the window: one of the device's, when it's the keyboard.</summary>
        public void Key(PhysicalKey key, bool down)
        {
            if (IsKeyboard)
                _input.Key(key, down);
        }

        /// <summary>Starts the test, or starts it again: the count-in, then the notes.</summary>
        [RelayCommand(CanExecute = nameof(CanBegin))]
        private void Begin()
        {
            _taps.Clear();
            Measured = null;
            IsStatusWarning = false;
            Message = null;
            _startedAt = _clock();
            State = DeviceTestState.Running;
            SaveCommand.NotifyCanExecuteChanged();
            Tick();
            _timer.Start();
        }

        private bool CanBegin() => State != DeviceTestState.Running;

        protected override bool CanSave() => Measured is not null && State == DeviceTestState.Done;

        protected override bool OnSave()
        {
            if (Measured is not { } offset)
                return false;

            if (!_input.Save(offset))
            {
                Message = SharedStrings.CalibrationDeviceSaveFailed;
                return false;
            }

            return true;
        }

        /// <summary>A press of the device, at <paramref name="time"/> on the input clock: counted while the test runs.</summary>
        private void OnPressed(double time)
        {
            if (State == DeviceTestState.Running)
                _taps.Add(time - _startedAt);
        }

        /// <summary>The count-in and the note the test is on, and its end once the last note's tap has had time to come.</summary>
        private void Tick()
        {
            double at = _clock() - _startedAt;
            if (at >= CalibrationChart.DeviceEndSeconds)
            {
                Finish();
                return;
            }

            // The first bar to get ready, the second counted down; then the notes, each from half a beat before it.
            double beat = at / CalibrationChart.BeatSeconds;
            const int BeatsPerBar = 4;
            if (beat < BeatsPerBar)
            {
                CountIn = SharedStrings.CalibrationGetReady;
                Status = SharedStrings.CalibrationGetReady;
            }
            else if (beat < CalibrationChart.DeviceLeadBeats)
            {
                CountIn = ((int)(CalibrationChart.DeviceLeadBeats - Math.Floor(beat))).ToString(CultureInfo.CurrentCulture);
                Status = SharedStrings.CalibrationGetReady;
            }
            else
            {
                CountIn = null;
                int note = Math.Clamp((int)Math.Floor(beat + 0.5) - CalibrationChart.DeviceLeadBeats + 1, 1, CalibrationChart.DeviceNoteCount);
                Status = string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationNoteProgressFormat, note, CalibrationChart.DeviceNoteCount);
            }
        }

        private void Finish()
        {
            _timer.Stop();
            CountIn = null;
            State = DeviceTestState.Done;
            if (TapTest.Estimate(_notes, _taps) is not { } estimate)
            {
                IsStatusWarning = true;
                Status = SharedStrings.CalibrationDeviceTooFew;
                SaveCommand.NotifyCanExecuteChanged();
                return;
            }

            int measured = (int)Math.Round(estimate.OffsetMilliseconds);
            int offset = Math.Clamp(measured, -DeviceTuning.MaxOffsetMs, DeviceTuning.MaxOffsetMs);
            bool wide = estimate.SpreadMilliseconds > TapTest.WideSpreadMilliseconds;
            IsStatusWarning = wide || offset != measured;
            Status = string.Format(
                CultureInfo.CurrentCulture,
                wide ? SharedStrings.CalibrationDeviceResultWideFormat : SharedStrings.CalibrationDeviceResultFormat,
                measured,
                Math.Round(estimate.SpreadMilliseconds),
                estimate.Kept);
            if (offset != measured)
                Status += " " + string.Format(CultureInfo.CurrentCulture, SharedStrings.CalibrationDeviceClampedFormat, offset);
            Measured = offset;
            SaveCommand.NotifyCanExecuteChanged();
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _listening?.Dispose();
            _listening = null;
        }
    }
}
