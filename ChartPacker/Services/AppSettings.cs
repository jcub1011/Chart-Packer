using System.Text.Json.Serialization;
using ChartPacker.Core.Jobs;

namespace ChartPacker.Services
{
    /// <summary>User settings persisted to <c>settings.json</c>. See spec §8.</summary>
    public sealed class AppSettings
    {
        public const double DefaultLibraryPaneWidth = 320;
        public const double MinLibraryPaneWidth = 200;
        public const int DefaultScanMaxDepth = 2;
        public const int MaxScanMaxDepth = 10;
        public const double DefaultMasterVolume = 1;
        public const OverwritePolicy DefaultOverwritePolicy = OverwritePolicy.Skip;

        /// <summary>Width of the library (left) panel, in device-independent pixels.</summary>
        public double LibraryPaneWidth { get; set; } = DefaultLibraryPaneWidth;

        /// <summary>How many folder levels below the picked folder the library scan searches.</summary>
        public int ScanMaxDepth { get; set; } = DefaultScanMaxDepth;

        /// <summary>The last folder picked in the library panel. It is restored at launch but not scanned.</summary>
        public string? LastFolder { get; set; }

        /// <summary>Where Pack and Unpack write. <see langword="null"/> means the scanned folder.</summary>
        public string? OutputFolder { get; set; }

        /// <summary>What Pack and Unpack do when an output already exists.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<OverwritePolicy>))]
        public OverwritePolicy OverwritePolicy { get; set; } = DefaultOverwritePolicy;

        /// <summary>Whether Pack writes every .sng directly in the output folder instead of mirroring the scanned folder's layout.</summary>
        public bool FlatPackOutput { get; set; }

        /// <summary>The audio player's master volume, from 0 to 1.</summary>
        public double MasterVolume { get; set; } = DefaultMasterVolume;

        /// <summary>Whether the player reads and draws waveforms. Off, no track is read ahead of playback.</summary>
        public bool ShowWaveforms { get; set; } = true;

        /// <summary>Whether the spectrum is drawn behind the details panel. Off, it isn't worked out at all.</summary>
        public bool ShowSpectrum { get; set; } = true;
    }
}
