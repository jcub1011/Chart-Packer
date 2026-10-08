using ChartPacker.Audio.Mixing;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>One stem in the transport bar's Stems menu (charter-spec C3 item 6): its mute, solo and volume.</summary>
    public sealed partial class StemRowViewModel(MixerStem stem) : ViewModelBase
    {
        private readonly MixerStem _stem = stem;

        public string Name => _stem.Name;

        [ObservableProperty]
        public partial bool IsMuted { get; set; } = stem.IsMuted;

        [ObservableProperty]
        public partial bool IsSolo { get; set; } = stem.IsSolo;

        /// <summary>The stem's volume, from 0 to 100%.</summary>
        [ObservableProperty]
        public partial double VolumePercent { get; set; } = stem.Volume * 100;

        partial void OnIsMutedChanged(bool value) => _stem.IsMuted = value;

        partial void OnIsSoloChanged(bool value) => _stem.IsSolo = value;

        partial void OnVolumePercentChanged(double value) => _stem.Volume = (float)(System.Math.Clamp(value, 0, 100) / 100);
    }

    /// <summary>A loop region, in seconds of audio (charter-spec C10 item 2).</summary>
    public sealed record LoopRegion(double Start, double End);
}
