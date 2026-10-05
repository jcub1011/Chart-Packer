using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Dedup;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Library;

namespace ChartPacker.Services
{
    /// <summary>User settings persisted to <c>settings.json</c>. See spec §8.</summary>
    public sealed class AppSettings
    {
        public const double DefaultLibraryPaneWidth = 320;
        public const double MinLibraryPaneWidth = 200;
        public const int DefaultScanMaxDepth = 2;
        public const int MaxScanMaxDepth = 10;
        // Reading a song is mostly parsing its chart, so a reader per processor keeps every core busy.
        public static readonly int DefaultScanParallelism = Environment.ProcessorCount;
        public const int MinScanParallelism = 1;
        public const double DefaultMasterVolume = 1;
        public const OverwritePolicy DefaultOverwritePolicy = OverwritePolicy.Skip;
        public const DedupKindPreference DefaultDedupKeepPreference = DedupKindPreference.PreferSng;
        public const double DefaultHighwayNoteSpeed = 1;
        public const double MinHighwayNoteSpeed = 0.5;
        public const double MaxHighwayNoteSpeed = 3;
        public const HighwayPlacement DefaultHighwayPlacement = HighwayPlacement.Background;
        public const WaveformFidelity DefaultWaveformFidelity = WaveformFidelity.High;
        public const ScrubStep DefaultScrubStep = ScrubStep.Quarter;
        public const double DefaultFineScrubMilliseconds = 5;
        public const double MinFineScrubMilliseconds = 0.5;
        public const double MaxFineScrubMilliseconds = 20;
        public const double DefaultLaneFineScrubFactor = 5;
        public const double MinLaneFineScrubFactor = 2;
        public const double MaxLaneFineScrubFactor = 20;
        public const double DefaultMergePreviewFraction = 0.4;
        public const double MinMergePreviewFraction = 0.2;
        public const double MaxMergePreviewFraction = 0.8;
        public const double DefaultValidatorListFraction = 0.4;
        public const double MinValidatorListFraction = 0.2;
        public const double MaxValidatorListFraction = 0.8;

        /// <summary>Width of the library (left) panel, in device-independent pixels.</summary>
        public double LibraryPaneWidth { get; set; } = DefaultLibraryPaneWidth;

        /// <summary>Whether the merge songs editor was maximized when it was last closed.</summary>
        public bool MergeWindowMaximized { get; set; }

        /// <summary>Share of the merge songs editor's width given to the preview (right) panel, from 0 to 1.</summary>
        public double MergePreviewFraction { get; set; } = DefaultMergePreviewFraction;

        /// <summary>Share of the Validator window's width given to the song list (left) panel, from 0 to 1.</summary>
        public double ValidatorListFraction { get; set; } = DefaultValidatorListFraction;

        /// <summary>How many folder levels below the picked folder the library scan searches.</summary>
        public int ScanMaxDepth { get; set; } = DefaultScanMaxDepth;

        /// <summary>How many folders the library scan searches, and how many songs it reads, at once.</summary>
        public int ScanParallelism { get; set; } = DefaultScanParallelism;

        /// <summary>The last folder picked in the library panel. It is restored at launch but not scanned.</summary>
        public string? LastFolder { get; set; }

        /// <summary>Where Pack and Unpack write. <see langword="null"/> means the scanned folder.</summary>
        public string? OutputFolder { get; set; }

        /// <summary>What Pack and Unpack do when an output already exists.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<OverwritePolicy>))]
        public OverwritePolicy OverwritePolicy { get; set; } = DefaultOverwritePolicy;

        /// <summary>Whether Pack writes every .sng directly in the output folder instead of mirroring the scanned folder's layout.</summary>
        public bool FlatPackOutput { get; set; }

        /// <summary>Where files saved out of a song go. <see langword="null"/> means the system's Downloads folder.</summary>
        public string? DownloadFolder { get; set; }

        /// <summary>Whether each download asks where to save, starting in <see cref="DownloadFolder"/>.</summary>
        public bool AskDownloadLocation { get; set; }

        /// <summary>Which copy the Deduplicate window keeps by default when a song is both a folder and a .sng.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<DedupKindPreference>))]
        public DedupKindPreference DedupKeepPreference { get; set; } = DefaultDedupKeepPreference;

        /// <summary>What the library list is sorted by.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<LibrarySortField>))]
        public LibrarySortField LibrarySortField { get; set; }

        /// <summary>Whether the library list is sorted from last to first.</summary>
        public bool LibrarySortDescending { get; set; }

        /// <summary>The audio player's master volume, from 0 to 1.</summary>
        public double MasterVolume { get; set; } = DefaultMasterVolume;

        /// <summary>Whether the player reads and draws waveforms. Off, no track is read ahead of playback.</summary>
        public bool ShowWaveforms { get; set; } = true;

        /// <summary>Whether each highway and the lyrics lane draw their track's waveform behind their notes, while waveforms are shown.</summary>
        public bool ShowLaneWaveforms { get; set; } = true;

        /// <summary>How finely the tracks' waveforms are read.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<WaveformFidelity>))]
        public WaveformFidelity WaveformFidelity { get; set; } = DefaultWaveformFidelity;

        /// <summary>Whether the spectrum is drawn behind the details panel. Off, it isn't worked out at all.</summary>
        public bool ShowSpectrum { get; set; } = true;

        /// <summary>Whether the chart's notes are drawn.</summary>
        public bool ShowNoteHighway { get; set; } = true;

        /// <summary>Where the chart's notes are drawn: over the background, or in a row of their own.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<HighwayPlacement>))]
        public HighwayPlacement HighwayPlacement { get; set; } = DefaultHighwayPlacement;

        /// <summary>How fast the highway's notes fall, as a multiple of the normal speed.</summary>
        public double HighwayNoteSpeed { get; set; } = DefaultHighwayNoteSpeed;

        /// <summary>How far Ctrl and the wheel move the audio over the seek bar or a lane: a note of this length on the chart's grid.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<ScrubStep>))]
        public ScrubStep ScrubStep { get; set; } = DefaultScrubStep;

        /// <summary>How far each pixel dragged with Ctrl held on the seek bar moves the audio, in milliseconds, whatever the song's length.</summary>
        public double FineScrubMilliseconds { get; set; } = DefaultFineScrubMilliseconds;

        /// <summary>How many times less far a drag with Ctrl held moves a highway's or the lyrics' notes than a plain drag.</summary>
        public double LaneFineScrubFactor { get; set; } = DefaultLaneFineScrubFactor;

        /// <summary>The difficulty last chosen for each instrument, by name. An instrument not listed shows Expert.</summary>
        public Dictionary<string, string>? HighwayDifficulties { get; set; }
    }
}
