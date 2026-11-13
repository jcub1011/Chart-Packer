using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartShared.Core.Optimization;
using ChartShared.Core.Settings;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>One choice in a drop-down. It's shown as its label.</summary>
    public sealed record OptimizeChoice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>One strategy's switch: whether it's on, whether its backend can run at all, and what it's expected to save.</summary>
    public sealed partial class OptimizeStrategyToggle : ObservableObject
    {
        public OptimizeStrategyToggle(OptimizeStrategy strategy, string title, string iconKey, string? formatTag, string? description, string? unavailableReason)
        {
            Strategy = strategy;
            Title = title;
            IconKey = iconKey;
            FormatTag = formatTag;
            Description = description;
            UnavailableReason = unavailableReason;
        }

        public OptimizeStrategy Strategy { get; }

        public string Title { get; }

        /// <summary>The key of the line icon beside the title.</summary>
        public string IconKey { get; }

        /// <summary>What the files become ("Opus"), for the chip beside the title; <see langword="null"/> when there's no one format.</summary>
        public string? FormatTag { get; }

        public bool HasFormatTag => FormatTag is not null;

        /// <summary>What isn't plain from the title, for the tooltip; <see langword="null"/> when the title says it all.</summary>
        public string? Description { get; }

        public bool HasDescription => Description is not null;

        /// <summary>Why the strategy can't run, or <see langword="null"/> when it can.</summary>
        public string? UnavailableReason { get; }

        public bool IsAvailable => UnavailableReason is null;

        public bool IsUnavailable => UnavailableReason is not null;

        public string UnavailableText => UnavailableReason is { } reason ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeUnavailableFormat, reason) : string.Empty;

        /// <summary>Whether the strategy is switched on. Its switch is disabled while it's unavailable.</summary>
        [ObservableProperty]
        public partial bool IsOn { get; set; }

        /// <summary>What the strategy saves across the charts, as far as is known; empty while it's off or nothing's known.</summary>
        [ObservableProperty]
        public partial string SavingText { get; set; } = string.Empty;

        /// <summary>Whether <see cref="SavingText"/> still leans on predictions, so it's shown as approximate.</summary>
        [ObservableProperty]
        public partial bool IsSavingPredicted { get; set; }
    }

    /// <summary>
    /// The Optimize window's settings: what to shrink and how, how small a saving is worth acting on, and where the changed
    /// charts go. <see cref="Changed"/> says when any of it changes.
    /// </summary>
    public sealed partial class OptimizeSettingsViewModel : ViewModelBase
    {
        private static readonly int[] BitratePresets = [64, 80, 96, 128, 160, 192, 256];

        // Not offered in the window, so it's carried from the saved settings to the run, and back when they're saved.
        private int _parallelism;
        private bool _applying;

        /// <param name="settings">What the window was last set to.</param>
        /// <param name="unavailableReason">Why a strategy can't run, or null when it can.</param>
        public OptimizeSettingsViewModel(OptimizeSettings settings, Func<OptimizeStrategy, string?> unavailableReason)
        {
            OptimizeStrategyToggle Toggle(OptimizeStrategy strategy, string title, string iconKey, string? formatTag, string? description) =>
                new(strategy, title, iconKey, formatTag, description, unavailableReason(strategy));

            Metadata = Toggle(OptimizeStrategy.Metadata, Strings.OptimizeMetadataLabel, "InfoStroke", null, Strings.OptimizeMetadataDescription);
            Audio = Toggle(OptimizeStrategy.Audio, Strings.OptimizeAudioLabel, "AudioStroke", Strings.OptimizeAudioFormatTag, Strings.OptimizeAudioDescription);
            StemMixdown = Toggle(OptimizeStrategy.StemMixdown, Strings.OptimizeStemMixdownLabel, "MergeStroke", Strings.OptimizeStemMixdownFormatTag, Strings.OptimizeStemMixdownDescription);
            Video = Toggle(OptimizeStrategy.Video, Strings.OptimizeVideoLabel, "VideoStroke", Strings.OptimizeVideoFormatTag, Strings.OptimizeVideoDescription);
            AlbumArt = Toggle(OptimizeStrategy.AlbumArt, Strings.OptimizeAlbumArtLabel, "AlbumArtStroke", Strings.OptimizeImageFormatTag, null);
            BackgroundArt = Toggle(OptimizeStrategy.BackgroundArt, Strings.OptimizeBackgroundArtLabel, "ImageStroke", Strings.OptimizeImageFormatTag, null);
            Toggles = [Metadata, Audio, StemMixdown, Video, AlbumArt, BackgroundArt];
            foreach (var toggle in Toggles)
                toggle.PropertyChanged += OnToggleChanged;

            BitrateChoices = [.. BitratePresets.Select(BitrateChoice)];
            MixdownBitrateChoices = [.. BitratePresets.Select(BitrateChoice)];
            VideoTargets =
            [
                new(VideoTarget.Sd, Strings.OptimizeVideoSd),
                new(VideoTarget.Hd, Strings.OptimizeVideoHd),
                new(VideoTarget.Fhd, Strings.OptimizeVideoFhd),
                new(VideoTarget.Qhd, Strings.OptimizeVideoQhd),
                new(VideoTarget.Uhd, Strings.OptimizeVideoUhd),
            ];
            // Quickest first.
            VideoSpeeds =
            [
                new(VideoEncodeSpeed.Fastest, Strings.OptimizeVideoSpeedFastest),
                new(VideoEncodeSpeed.Fast, Strings.OptimizeVideoSpeedFast),
                new(VideoEncodeSpeed.Balanced, Strings.OptimizeVideoSpeedBalanced),
                new(VideoEncodeSpeed.Slow, Strings.OptimizeVideoSpeedSlow),
                new(VideoEncodeSpeed.Best, Strings.OptimizeVideoSpeedBest),
            ];
            AlbumArtLimits = [.. new (int? W, int? H)[] { (null, null), (256, 256), (512, 512), (1024, 1024) }.Select(LimitChoice)];
            BackgroundArtLimits = [.. new (int? W, int? H)[] { (null, null), (854, 480), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) }.Select(LimitChoice)];

            Apply(settings);
        }

        /// <summary>A setting changed. Raised once per change, on the UI thread.</summary>
        public event Action? Changed;

        public OptimizeStrategyToggle Metadata { get; }

        public OptimizeStrategyToggle Audio { get; }

        public OptimizeStrategyToggle StemMixdown { get; }

        public OptimizeStrategyToggle Video { get; }

        public OptimizeStrategyToggle AlbumArt { get; }

        public OptimizeStrategyToggle BackgroundArt { get; }

        public IReadOnlyList<OptimizeStrategyToggle> Toggles { get; }

        /// <summary>Whether any strategy is on.</summary>
        public bool AnyOn => Toggles.Any(t => t.IsOn);

        public ObservableCollection<OptimizeChoice<int>> BitrateChoices { get; }

        public ObservableCollection<OptimizeChoice<int>> MixdownBitrateChoices { get; }

        public IReadOnlyList<OptimizeChoice<VideoTarget>> VideoTargets { get; }

        public IReadOnlyList<OptimizeChoice<VideoEncodeSpeed>> VideoSpeeds { get; }

        public ObservableCollection<OptimizeChoice<(int? W, int? H)>> AlbumArtLimits { get; }

        public ObservableCollection<OptimizeChoice<(int? W, int? H)>> BackgroundArtLimits { get; }

        // The metadata steps.

        [ObservableProperty]
        public partial bool PruneEmptyParts { get; set; }

        [ObservableProperty]
        public partial bool StripEditorEvents { get; set; }

        [ObservableProperty]
        public partial bool DropEmptyValues { get; set; }

        [ObservableProperty]
        public partial bool ReplaceLegacyKeys { get; set; }

        // Audio.

        [ObservableProperty]
        public partial OptimizeChoice<int>? SelectedBitrate { get; set; }

        [ObservableProperty]
        public partial OptimizeChoice<int>? SelectedMixdownBitrate { get; set; }

        // Video.

        [ObservableProperty]
        public partial OptimizeChoice<VideoTarget>? SelectedVideoTarget { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VideoCqText))]
        public partial double VideoCq { get; set; }

        public string VideoCqText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeVideoCqFormat, (int)Math.Round(VideoCq));

        [ObservableProperty]
        public partial OptimizeChoice<VideoEncodeSpeed>? SelectedVideoSpeed { get; set; }

        [ObservableProperty]
        public partial bool VideoConvertUnsupported { get; set; }

        // Images.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AlbumArtQualityText))]
        public partial double AlbumArtQuality { get; set; }

        public string AlbumArtQualityText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeImageQualityFormat, (int)Math.Round(AlbumArtQuality));

        [ObservableProperty]
        public partial OptimizeChoice<(int? W, int? H)>? SelectedAlbumArtLimit { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BackgroundArtQualityText))]
        public partial double BackgroundArtQuality { get; set; }

        public string BackgroundArtQualityText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizeImageQualityFormat, (int)Math.Round(BackgroundArtQuality));

        [ObservableProperty]
        public partial OptimizeChoice<(int? W, int? H)>? SelectedBackgroundArtLimit { get; set; }

        // The margin, as a percentage.

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MarginText))]
        public partial double MarginPercent { get; set; }

        public string MarginText => string.Format(CultureInfo.CurrentCulture, Strings.OptimizePercentFormat, (int)Math.Round(MarginPercent));

        // Where the changed charts go.

        /// <summary>Changed charts are written to <see cref="OutputFolder"/>, and the library is left as it is.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsInPlace))]
        public partial bool UseOutputFolder { get; set; }

        /// <summary>Charts are changed where they are, which can't be undone.</summary>
        public bool IsInPlace
        {
            get => !UseOutputFolder;
            set => UseOutputFolder = !value;
        }

        [ObservableProperty]
        public partial string? OutputFolder { get; set; }

        /// <summary>Puts every setting to <paramref name="s"/>, as one change.</summary>
        public void Apply(OptimizeSettings s)
        {
            ArgumentNullException.ThrowIfNull(s);
            _applying = true;
            try
            {
                _parallelism = s.Parallelism;

                Set(Metadata, s.MetadataEnabled);
                PruneEmptyParts = s.PruneEmptyParts;
                StripEditorEvents = s.StripEditorEvents;
                DropEmptyValues = s.DropEmptyValues;
                ReplaceLegacyKeys = s.ReplaceLegacyKeys;
                Set(Audio, s.AudioEnabled);
                SelectedBitrate = Pick(BitrateChoices, s.AudioBitrateKbps, BitrateChoice);
                Set(StemMixdown, s.StemMixdownEnabled);
                SelectedMixdownBitrate = Pick(MixdownBitrateChoices, s.StemMixdownBitrateKbps, BitrateChoice);
                Set(Video, s.VideoEnabled);
                SelectedVideoTarget = VideoTargets.First(c => c.Value == s.VideoTarget);
                VideoCq = s.VideoCq;
                SelectedVideoSpeed = VideoSpeeds.FirstOrDefault(c => c.Value == s.VideoSpeed) ?? VideoSpeeds.First(c => c.Value == VideoEncodeSpeed.Balanced);
                VideoConvertUnsupported = s.VideoConvertUnsupported;
                Set(AlbumArt, s.AlbumArtEnabled);
                AlbumArtQuality = s.AlbumArtQuality;
                SelectedAlbumArtLimit = Pick(AlbumArtLimits, (s.AlbumArtMaxWidth, s.AlbumArtMaxHeight), LimitChoice);
                Set(BackgroundArt, s.BackgroundArtEnabled);
                BackgroundArtQuality = s.BackgroundArtQuality;
                SelectedBackgroundArtLimit = Pick(BackgroundArtLimits, (s.BackgroundArtMaxWidth, s.BackgroundArtMaxHeight), LimitChoice);
                MarginPercent = Math.Round(s.SavingsMargin * 100);
                UseOutputFolder = s.UseOutputFolder;
                OutputFolder = s.OutputFolder;
            }
            finally
            {
                _applying = false;
            }

            Changed?.Invoke();
        }

        /// <summary>The settings as they are now, to save.</summary>
        public OptimizeSettings ToSettings() => new()
        {
            MetadataEnabled = Metadata.IsOn,
            PruneEmptyParts = PruneEmptyParts,
            StripEditorEvents = StripEditorEvents,
            DropEmptyValues = DropEmptyValues,
            ReplaceLegacyKeys = ReplaceLegacyKeys,
            AudioEnabled = Audio.IsOn,
            AudioBitrateKbps = SelectedBitrate?.Value ?? AudioOptimizeOptions.DefaultBitrateKbps,
            StemMixdownEnabled = StemMixdown.IsOn,
            StemMixdownBitrateKbps = SelectedMixdownBitrate?.Value ?? AudioOptimizeOptions.DefaultBitrateKbps,
            VideoEnabled = Video.IsOn,
            VideoTarget = SelectedVideoTarget?.Value ?? VideoTarget.Fhd,
            VideoCq = (int)Math.Round(VideoCq),
            VideoConvertUnsupported = VideoConvertUnsupported,
            VideoSpeed = SelectedVideoSpeed?.Value ?? VideoEncodeSpeed.Balanced,
            AlbumArtEnabled = AlbumArt.IsOn,
            AlbumArtQuality = (int)Math.Round(AlbumArtQuality),
            AlbumArtMaxWidth = SelectedAlbumArtLimit?.Value.W,
            AlbumArtMaxHeight = SelectedAlbumArtLimit?.Value.H,
            BackgroundArtEnabled = BackgroundArt.IsOn,
            BackgroundArtQuality = (int)Math.Round(BackgroundArtQuality),
            BackgroundArtMaxWidth = SelectedBackgroundArtLimit?.Value.W,
            BackgroundArtMaxHeight = SelectedBackgroundArtLimit?.Value.H,
            SavingsMargin = MarginPercent / 100,
            UseOutputFolder = UseOutputFolder,
            OutputFolder = OutputFolder,
            Parallelism = _parallelism,
        };

        /// <summary>The options a run takes, as the settings are now.</summary>
        public OptimizeOptions ToOptions() => ToSettings().ToOptions(UseOutputFolder ? OutputFolder : null);

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (!_applying)
                Changed?.Invoke();
        }

        private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OptimizeStrategyToggle.IsOn) && !_applying)
                Changed?.Invoke();
        }

        // A strategy whose backend is missing stays off, whatever was saved.
        private static void Set(OptimizeStrategyToggle toggle, bool on) => toggle.IsOn = on && toggle.IsAvailable;

        private static OptimizeChoice<int> BitrateChoice(int kbps) =>
            new(kbps, string.Format(CultureInfo.CurrentCulture, Strings.OptimizeBitrateFormat, kbps));

        private static OptimizeChoice<(int? W, int? H)> LimitChoice((int? W, int? H) limit) => new(limit, limit is { W: { } w, H: { } h }
            ? string.Format(CultureInfo.CurrentCulture, Strings.OptimizeSizeLimitFormat, w, h)
            : Strings.OptimizeNoLimit);

        // The choice for a value, adding one when what was saved isn't among the presets.
        private static OptimizeChoice<T> Pick<T>(ObservableCollection<OptimizeChoice<T>> choices, T value, Func<T, OptimizeChoice<T>> make)
        {
            var existing = choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value));
            if (existing is not null)
                return existing;

            var added = make(value);
            choices.Add(added);
            return added;
        }
    }
}
