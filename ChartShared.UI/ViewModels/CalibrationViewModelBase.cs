using System;
using ChartShared.Core.Calibration;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>
    /// What the calibration tool's window (charter-spec C11) shows, whichever test it's running: the audio/visual latency
    /// (<see cref="CalibrationViewModel"/>) or an input device's (<see cref="DeviceCalibrationViewModel"/>). The highway on
    /// its left draws <see cref="Scene"/> at <see cref="Now"/>; the test's own controls are on its right.
    /// </summary>
    public abstract partial class CalibrationViewModelBase : ViewModelBase, IDisposable
    {
        /// <summary>Raised when the tool has saved, and its window should close.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>The highway's notes.</summary>
        public abstract HighwayScene Scene { get; }

        /// <summary>The window's title, naming the test.</summary>
        public abstract string Title { get; }

        /// <summary>A note across the top of the highway, such as that calibration is held off; null for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBanner))]
        public partial string? Banner { get; protected set; }

        /// <summary>What to show large in the middle of the highway, such as the count-in; null for nothing.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCountIn))]
        public partial string? CountIn { get; protected set; }

        /// <summary>Why the test can't run, or what it measured couldn't be saved; null when all's well.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; protected set; }

        public bool HasBanner => Banner is not null;

        public bool HasCountIn => CountIn is not null;

        public bool HasMessage => Message is not null;

        /// <summary>Where the highway is drawn now, read each frame.</summary>
        public abstract TimeSpan Now();

        /// <summary>Starts the test's clock and devices once its window is open. Never throws.</summary>
        public abstract void Start();

        public abstract void Dispose();

        /// <summary>Stores what the test found, and closes the tool.</summary>
        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save()
        {
            if (OnSave())
                CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stores what the test found. Gives false, having said why in <see cref="Message"/>, when it couldn't.</summary>
        protected abstract bool OnSave();

        /// <summary>Whether there's anything to save yet.</summary>
        protected virtual bool CanSave() => true;

        /// <summary>The tool's highway: one 5-fret lane of <paramref name="notes"/> on <see cref="CalibrationChart.Tempo"/>, from <paramref name="lead"/>.</summary>
        protected static HighwayScene SceneOf(ChartNote[] notes, TimeSpan lead)
        {
            var tempo = CalibrationChart.Tempo;
            return new HighwayScene(
                [new HighwayLane(ChartInstrumentKind.FiveFret, HighwayLane.LaneCountOf(ChartInstrumentKind.FiveFret, false), "", notes, tempo, lead)],
                tempo,
                lead);
        }
    }
}
