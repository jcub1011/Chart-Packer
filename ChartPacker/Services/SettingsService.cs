using System;
using ChartShared.Core.Library;
using ChartShared.Core.Settings;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <summary>Loads and saves <see cref="AppSettings"/> as JSON, through <see cref="JsonSettingsFile{T}"/>.</summary>
    /// <remarks>
    /// The file path is always passed in, never defaulted, so nothing but the composition root
    /// (which passes <see cref="Core.UserConfigPaths.SettingsFile"/>) can touch the real settings.
    /// Tests must pass a temp file.
    /// </remarks>
    public sealed class SettingsService
    {
        private readonly JsonSettingsFile<AppSettings> _file;

        private SettingsService(JsonSettingsFile<AppSettings> file, AppSettings settings)
        {
            _file = file;
            Settings = settings;
        }

        /// <summary>The slowest <see cref="PlaybackSpeed"/>: 1%.</summary>
        public const double MinPlaybackSpeed = 0.01;

        /// <summary>The fastest <see cref="PlaybackSpeed"/>: 1000%.</summary>
        public const double MaxPlaybackSpeed = 10;

        public AppSettings Settings { get; }

        /// <summary>
        /// How fast the players play, as a fraction: 1 is the audio's own speed. Shared by every song and player, but
        /// kept for this run only; never written to the settings file, so each run starts at 1.
        /// </summary>
        public double PlaybackSpeed { get; set; } = 1;

        /// <summary>Loads settings from <paramref name="filePath"/>, falling back to defaults when it's missing or unreadable.</summary>
        public static SettingsService Load(string filePath)
        {
            var file = new JsonSettingsFile<AppSettings>(filePath, AppSettingsJsonContext.Default.AppSettings);
            var settings = file.Load();
            Sanitize(settings);
            return new SettingsService(file, settings);
        }

        /// <summary>Writes the settings changed in this run to the file, keeping other processes' changes to the rest.</summary>
        public void Save() => _file.Save(Settings);

        private static void Sanitize(AppSettings settings)
        {
            if (!double.IsFinite(settings.LibraryPaneWidth) || settings.LibraryPaneWidth < AppSettings.MinLibraryPaneWidth)
                settings.LibraryPaneWidth = AppSettings.DefaultLibraryPaneWidth;

            if (!double.IsFinite(settings.MergePreviewFraction)
                || settings.MergePreviewFraction < AppSettings.MinMergePreviewFraction
                || settings.MergePreviewFraction > AppSettings.MaxMergePreviewFraction)
                settings.MergePreviewFraction = AppSettings.DefaultMergePreviewFraction;

            if (!double.IsFinite(settings.ValidatorListFraction)
                || settings.ValidatorListFraction < AppSettings.MinValidatorListFraction
                || settings.ValidatorListFraction > AppSettings.MaxValidatorListFraction)
                settings.ValidatorListFraction = AppSettings.DefaultValidatorListFraction;

            settings.ScanMaxDepth = Math.Clamp(settings.ScanMaxDepth, 0, AppSettings.MaxScanMaxDepth);

            if (settings.ScanParallelism < AppSettings.MinScanParallelism)
                settings.ScanParallelism = AppSettings.DefaultScanParallelism;

            if (string.IsNullOrWhiteSpace(settings.LastFolder))
                settings.LastFolder = null;

            if (string.IsNullOrWhiteSpace(settings.OutputFolder))
                settings.OutputFolder = null;

            if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
                settings.DownloadFolder = null;

            if (!Enum.IsDefined(settings.OverwritePolicy))
                settings.OverwritePolicy = AppSettings.DefaultOverwritePolicy;

            if (!Enum.IsDefined(settings.DedupKeepPreference))
                settings.DedupKeepPreference = AppSettings.DefaultDedupKeepPreference;

            if (!Enum.IsDefined(settings.WaveformFidelity))
                settings.WaveformFidelity = AppSettings.DefaultWaveformFidelity;

            if (!Enum.IsDefined(settings.ScrubStep))
                settings.ScrubStep = AppSettings.DefaultScrubStep;

            if (!Enum.IsDefined(settings.LibrarySortField))
                settings.LibrarySortField = LibrarySortField.ScanOrder;

            settings.MasterVolume = double.IsFinite(settings.MasterVolume)
                ? Math.Clamp(settings.MasterVolume, 0, 1)
                : AppSettings.DefaultMasterVolume;

            settings.HighwayNoteSpeed = double.IsFinite(settings.HighwayNoteSpeed)
                ? Math.Clamp(settings.HighwayNoteSpeed, AppSettings.MinHighwayNoteSpeed, AppSettings.MaxHighwayNoteSpeed)
                : AppSettings.DefaultHighwayNoteSpeed;

            settings.FineScrubMilliseconds = double.IsFinite(settings.FineScrubMilliseconds)
                ? Math.Clamp(settings.FineScrubMilliseconds, AppSettings.MinFineScrubMilliseconds, AppSettings.MaxFineScrubMilliseconds)
                : AppSettings.DefaultFineScrubMilliseconds;

            settings.LaneFineScrubFactor = double.IsFinite(settings.LaneFineScrubFactor)
                ? Math.Clamp(settings.LaneFineScrubFactor, AppSettings.MinLaneFineScrubFactor, AppSettings.MaxLaneFineScrubFactor)
                : AppSettings.DefaultLaneFineScrubFactor;

            SanitizeMainWindowBounds(settings);
        }

        /// <summary>The main window's saved normal bounds, as <see cref="WindowBounds.FromSaved"/> reads them: dropped whole when half-saved.</summary>
        private static void SanitizeMainWindowBounds(AppSettings settings)
        {
            var bounds = WindowBounds.FromSaved(settings.MainWindowX, settings.MainWindowY, settings.MainWindowWidth, settings.MainWindowHeight);
            settings.MainWindowX = bounds?.Position.X;
            settings.MainWindowY = bounds?.Position.Y;
            settings.MainWindowWidth = bounds?.Size.Width;
            settings.MainWindowHeight = bounds?.Size.Height;
        }
    }
}
