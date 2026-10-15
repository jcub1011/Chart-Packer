using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A spectrogram band preset, as a lane's picker offers it.</summary>
    public sealed record BandChoice(SpectrogramBand Band, string Label);

    /// <summary>
    /// How one lane shows its audio (charter-spec C12–C14), from the lane header's audio button: which audio, as a
    /// waveform or a spectrogram (<c>W</c>), behind the lanes or beside them, the waveform in mono or stereo and its gain,
    /// the spectrogram's band and contrast, and the onsets beside the lanes and their sensitivity. Each change is kept
    /// with the lane layout.
    /// </summary>
    public sealed class LaneAudioViewModel : ViewModelBase
    {
        private readonly EditorLaneViewModel _lane;
        private readonly Action<LaneAudioOptions> _keep;

        // The source last resolved, and what from, as it's asked for every frame the lane is drawn. The tracks are part of
        // it, as the audio takes new ones in place when a track is added or removed.
        private (EditorAudio Audio, IReadOnlyList<SongTrack> Tracks, LaneAudioOptions Options, ChartInstrument Instrument, AudioSource? Source)? _source;

        // The picker's choices, made again only when they change: a new list clears the picker's selection first.
        private IReadOnlyList<AudioSourceChoice> _choices = [];

        internal LaneAudioViewModel(EditorLaneViewModel lane, LaneAudioOptions options, Action<LaneAudioOptions> keep)
        {
            _lane = lane;
            _keep = keep;
            Options = options;
            ToggleBackdropCommand = new RelayCommand(ToggleBackdrop);
            ResetGainCommand = new RelayCommand(() => GainDb = 0);
        }

        /// <summary>Raised when the options change, so the lane is drawn again.</summary>
        public event EventHandler? Changed;

        public LaneAudioOptions Options { get; private set; }

        private EditorAudio? Audio => _lane.Editor?.Audio;

        /// <summary>Whether the song has audio to show.</summary>
        public bool HasAudio => Audio?.HasAudio == true;

        /// <summary>The audio the lane is drawn over and its onsets come from, as picked; null without audio or a part.</summary>
        public AudioSource? Source
        {
            get
            {
                if (Audio is not { } audio || _lane.Track is not { } track)
                    return null;
                if (_source is { } known && ReferenceEquals(known.Audio, audio) && ReferenceEquals(known.Tracks, audio.Tracks)
                    && ReferenceEquals(known.Options, Options) && known.Instrument == track.Instrument)
                    return known.Source;
                var source = audio.SourceFor(Options, track.Instrument);
                _source = (audio, audio.Tracks, Options, track.Instrument, source);
                return source;
            }
        }

        /// <summary>The audio picker's choices: the part's own stems, the mix, and each stem.</summary>
        public IReadOnlyList<AudioSourceChoice> SourceChoices => _choices;

        public AudioSourceChoice? SelectedSource
        {
            get => SourceChoices.FirstOrDefault(c => string.Equals(c.Source, Options.Source, StringComparison.OrdinalIgnoreCase)) ?? SourceChoices.FirstOrDefault();
            set
            {
                if (value is not null)
                    Set(Options with { Source = value.Source });
            }
        }

        /// <summary>Waveform or spectrogram: the lane's own pick, else the setting.</summary>
        public LaneBackdrop Backdrop => Options.Backdrop ?? _lane.Editor?.DefaultBackdrop ?? LaneBackdrop.Waveform;

        public bool IsWaveform
        {
            get => Backdrop == LaneBackdrop.Waveform;
            set
            {
                if (value)
                    Set(Options with { Backdrop = LaneBackdrop.Waveform });
            }
        }

        public bool IsSpectrogram
        {
            get => Backdrop == LaneBackdrop.Spectrogram;
            set
            {
                if (value)
                    Set(Options with { Backdrop = LaneBackdrop.Spectrogram });
            }
        }

        public bool IsBehind
        {
            get => Options.Placement == LaneAudioPlacement.Behind;
            set
            {
                if (value)
                    Set(Options with { Placement = LaneAudioPlacement.Behind });
            }
        }

        public bool IsBeside
        {
            get => Options.Placement == LaneAudioPlacement.Beside;
            set
            {
                if (value)
                    Set(Options with { Placement = LaneAudioPlacement.Beside });
            }
        }

        public bool IsStereo
        {
            get => Options.Channels == LaneChannels.Stereo;
            set => Set(Options with { Channels = value ? LaneChannels.Stereo : LaneChannels.Mono });
        }

        /// <summary>How much louder than full scale the waveform is drawn, in dB.</summary>
        public double GainDb
        {
            get => Options.GainDb;
            set => Set(Options with { GainDb = Math.Round(value) });
        }

        public string GainText => string.Format(CultureInfo.CurrentCulture, Strings.LaneGainFormat, Options.GainDb);

        public static double MinGainDb => LaneAudioOptions.MinGainDb;

        public static double MaxGainDb => LaneAudioOptions.MaxGainDb;

        public static IReadOnlyList<BandChoice> BandChoices { get; } =
        [
            new(SpectrogramBand.Full, Strings.SpectrogramBandFull),
            new(SpectrogramBand.Kick, Strings.SpectrogramBandKick),
            new(SpectrogramBand.Snare, Strings.SpectrogramBandSnare),
            new(SpectrogramBand.Cymbals, Strings.SpectrogramBandCymbals),
            new(SpectrogramBand.Bass, Strings.SpectrogramBandBass),
        ];

        public BandChoice SelectedBand
        {
            get => BandChoices.First(b => b.Band == Options.Band);
            set
            {
                if (value is not null)
                    Set(Options with { Band = value.Band });
            }
        }

        /// <summary>The spectrogram's contrast floor, in dB: anything quieter is black.</summary>
        public double FloorDb
        {
            get => Options.FloorDb;
            set => Set(Options with { FloorDb = Math.Round(value) });
        }

        /// <summary>The spectrogram's contrast ceiling, in dB: anything louder is at full colour.</summary>
        public double CeilingDb
        {
            get => Options.CeilingDb;
            set => Set(Options with { CeilingDb = Math.Round(value) });
        }

        public string ContrastText => string.Format(CultureInfo.CurrentCulture, Strings.LaneContrastFormat, Options.FloorDb, Options.CeilingDb);

        public static double MinFloorDb => LaneAudioOptions.MinFloorDb;

        public static double MinCeilingDb => LaneAudioOptions.MinCeilingDb;

        public bool ShowOnsets
        {
            get => Options.ShowOnsets;
            set => Set(Options with { ShowOnsets = value });
        }

        /// <summary>How many onsets are shown and snapped to: from 0 (fewest) to 1 (most).</summary>
        public double OnsetSensitivity
        {
            get => Options.OnsetSensitivity;
            set => Set(Options with { OnsetSensitivity = Math.Round(value, 2) });
        }

        public string SensitivityText => string.Format(CultureInfo.CurrentCulture, Strings.LaneSensitivityFormat, Options.OnsetSensitivity);

        public IRelayCommand ToggleBackdropCommand { get; }

        public IRelayCommand ResetGainCommand { get; }

        /// <summary>Switches between the waveform and the spectrogram (<c>W</c>).</summary>
        public void ToggleBackdrop() => Set(Options with { Backdrop = IsWaveform ? LaneBackdrop.Spectrogram : LaneBackdrop.Waveform });

        /// <summary>Takes in the lane's part, the song's audio or the setting, which change the choices and what's shown.</summary>
        internal void Refresh()
        {
            var choices = Audio is { } audio && _lane.Track is { } track ? AudioSource.ChoicesFor(track.Instrument, audio.Tracks) : [];
            if (!choices.SequenceEqual(_choices))
            {
                _choices = choices;
                OnPropertyChanged(nameof(SourceChoices));
            }

            RaiseAll();
        }

        private void Set(LaneAudioOptions options)
        {
            options = options.Clamped();
            if (options == Options)
                return;
            Options = options;
            _keep(options);
            RaiseAll();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(HasAudio));
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(SelectedSource));
            OnPropertyChanged(nameof(Backdrop));
            OnPropertyChanged(nameof(IsWaveform));
            OnPropertyChanged(nameof(IsSpectrogram));
            OnPropertyChanged(nameof(IsBehind));
            OnPropertyChanged(nameof(IsBeside));
            OnPropertyChanged(nameof(IsStereo));
            OnPropertyChanged(nameof(GainDb));
            OnPropertyChanged(nameof(GainText));
            OnPropertyChanged(nameof(SelectedBand));
            OnPropertyChanged(nameof(FloorDb));
            OnPropertyChanged(nameof(CeilingDb));
            OnPropertyChanged(nameof(ContrastText));
            OnPropertyChanged(nameof(ShowOnsets));
            OnPropertyChanged(nameof(OnsetSensitivity));
            OnPropertyChanged(nameof(SensitivityText));
        }
    }
}
