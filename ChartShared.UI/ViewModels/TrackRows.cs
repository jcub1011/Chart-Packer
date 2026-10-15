using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>What plays a <see cref="TrackRowViewModel"/>'s track, and hears its mute, solo and volume.</summary>
    public interface ITrackMixer
    {
        /// <summary>Puts <paramref name="row"/>'s mute, solo and volume into the mix.</summary>
        void ApplySwitches(TrackRowViewModel row);

        /// <summary>Keeps <paramref name="row"/>'s volume, for as long as the mixer keeps volumes.</summary>
        void RememberVolume(TrackRowViewModel row);
    }

    /// <summary>
    /// One of a song's audio tracks in a track list (<see cref="Views.TrackListView"/>), with its mute and solo switches
    /// and its volume in the mix: Chart Packer's inspector's, and Chart Builder's Chart settings'.
    /// </summary>
    public sealed partial class TrackRowViewModel : ViewModelBase
    {
        private readonly ITrackMixer _owner;

        public TrackRowViewModel(ITrackMixer owner, SongTrack track)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(track);
            _owner = owner;
            Track = track;
            IsMuted = track.Stem == AudioStems.Preview;
        }

        public SongTrack Track { get; }

        public string Name => Track.StoredName;

        /// <summary><c>+</c> for a track a staged change adds, like an added file.</summary>
        public string? Marker => Track.IsStaged ? "+" : null;

        /// <summary>The track's file size, formatted; set by whoever knows the song's files.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSize))]
        public partial string? Size { get; set; }

        public bool HasSize => Size is not null;

        /// <summary>Saves a copy of the track's file, from its right-click menu; set by whoever knows where to save it. None, no menu item.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanDownload))]
        public partial IAsyncRelayCommand? DownloadCommand { get; set; }

        public bool CanDownload => DownloadCommand is not null;

        /// <summary>The right-click menu's item for <see cref="DownloadCommand"/>, naming the file; set with it.</summary>
        [ObservableProperty]
        public partial string? DownloadHeader { get; set; }

        [ObservableProperty]
        public partial bool IsMuted { get; set; }

        /// <summary>While any track is soloed, only soloed tracks are heard. A track is muted or soloed, never both.</summary>
        [ObservableProperty]
        public partial bool IsSolo { get; set; }

        /// <summary>How loud the track is in the mix, from 0 to 1.</summary>
        [ObservableProperty]
        public partial double Volume { get; set; } = 1;

        /// <summary>Why the track can't be played, once that's known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; set; }

        public bool HasError => Error is not null;

        /// <summary>The track's own waveform, at full level; empty until its audio has been read.</summary>
        [ObservableProperty]
        public partial Waveform Waveform { get; set; } = Waveform.Empty;

        /// <summary>Whether the track is in the mix as it's switched now: soloed, or unmuted with nothing soloed, and not turned all the way down.</summary>
        [ObservableProperty]
        public partial bool IsHeard { get; set; } = true;

        /// <summary>
        /// Marks each of <paramref name="rows"/> heard or not, as the mixer decides it: only the soloed ones while any is
        /// soloed, otherwise the unmuted ones; never one turned all the way down.
        /// </summary>
        public static void RefreshHeard(IEnumerable<TrackRowViewModel> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var list = rows.ToList();
            bool anySolo = list.Any(t => t.IsSolo);
            foreach (var row in list)
                row.IsHeard = (anySolo ? row.IsSolo : !row.IsMuted) && row.Volume > 0;
        }

        // Solo overrides mute in the mix, so having both on would read as if both applied. Turning
        // one on turns the other off.
        partial void OnIsMutedChanged(bool value)
        {
            if (value)
                IsSolo = false;
            _owner.ApplySwitches(this);
        }

        partial void OnIsSoloChanged(bool value)
        {
            if (value)
                IsMuted = false;
            _owner.ApplySwitches(this);
        }

        partial void OnVolumeChanged(double value)
        {
            _owner.RememberVolume(this);
            _owner.ApplySwitches(this);
        }
    }

    /// <summary>A track a staged change removes, shown struck through so that its removal can be undone.</summary>
    public sealed record RemovedTrackRow(string Name, string Size)
    {
        /// <summary>Its stem, e.g. <c>guitar</c>; null for a file that isn't a track.</summary>
        public string? Stem => AudioStems.StemOf(Name);
    }

    /// <summary>
    /// An entry of the Add track menu: a stem, labelled to say whether choosing it replaces the song's track for it. The
    /// command is given the stem.
    /// </summary>
    public sealed record StemOption(string Stem, string Label, ICommand Command);
}
